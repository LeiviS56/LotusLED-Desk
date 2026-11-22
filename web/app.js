'use strict';
/* =====================================================================
   LotusLED Desk — lógica de la interfaz
   ===================================================================== */
(() => {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const root = document.documentElement;

  // ------------------------------------------------------------- Color utils
  const Color = {
    hsvToRgb(h, s, v) {
      h = ((h % 360) + 360) % 360;
      const c = v * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = v - c;
      let r, g, b;
      if (h < 60) [r, g, b] = [c, x, 0];
      else if (h < 120) [r, g, b] = [x, c, 0];
      else if (h < 180) [r, g, b] = [0, c, x];
      else if (h < 240) [r, g, b] = [0, x, c];
      else if (h < 300) [r, g, b] = [x, 0, c];
      else [r, g, b] = [c, 0, x];
      return [r, g, b].map((n) => Math.round((n + m) * 255));
    },
    rgbToHsv(r, g, b) {
      r /= 255; g /= 255; b /= 255;
      const max = Math.max(r, g, b), min = Math.min(r, g, b), d = max - min;
      let h = 0;
      if (d) {
        if (max === r) h = 60 * (((g - b) / d) % 6);
        else if (max === g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
      }
      if (h < 0) h += 360;
      return [h, max ? d / max : 0, max];
    },
    toHex(rgb) { return '#' + rgb.map((v) => v.toString(16).padStart(2, '0')).join('').toUpperCase(); },
    fromHex(hex) {
      const m = /^#?([0-9a-f]{6}|[0-9a-f]{3})$/i.exec(String(hex).trim());
      if (!m) return null;
      let h = m[1];
      if (h.length === 3) h = [...h].map((c) => c + c).join('');
      return [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
    },
    /** Temperatura de color (K) → RGB (aprox. Tanner Helland). */
    kelvin(k) {
      const t = k / 100;
      let r, g, b;
      if (t <= 66) { r = 255; g = 99.4708025861 * Math.log(t) - 161.1195681661; }
      else { r = 329.698727446 * (t - 60) ** -0.1332047592; g = 288.1221695283 * (t - 60) ** -0.0755148492; }
      if (t >= 66) b = 255; else if (t <= 19) b = 0; else b = 138.5177312231 * Math.log(t - 10) - 305.0447927307;
      return [r, g, b].map((v) => clamp(Math.round(v), 0, 255));
    },
    same(a, b) { return a && b && a[0] === b[0] && a[1] === b[1] && a[2] === b[2]; },
  };

  // ------------------------------------------------------------- Estado
  const KEY = 'lotusled.settings.v1';
  const DEFAULTS = {
    power: true,
    color: [168, 85, 247],
    brightness: 80,
    mode: 'color',
    effect: null,
    speed: 50,
    tab: 'color',
    favorites: [[255, 45, 85], [0, 200, 255], [255, 140, 30], [120, 255, 120]],
    profile: 'auto',
    autostart: true,
    powerOnStart: true,
    powerOffShutdown: true,
    keepInBackground: true,
    autoConnect: true,
    restoreOnConnect: true,
    showAll: false,
    music: { source: 'system', mode: 'spectrum', sensitivity: 60, floor: 5 },
    screen: { region: 'full', algo: 'vivid', smoothing: 50, saturation: 130 },
  };
  const QUICK_COLORS = [
    ['Rojo', [255, 0, 0]], ['Coral', [255, 64, 64]], ['Naranja', [255, 110, 0]], ['Ámbar', [255, 180, 0]],
    ['Lima', [140, 255, 0]], ['Verde', [0, 255, 70]], ['Turquesa', [0, 255, 190]], ['Cian', [0, 210, 255]],
    ['Azul', [0, 60, 255]], ['Índigo', [80, 0, 255]], ['Morado', [168, 85, 247]], ['Magenta', [255, 0, 200]],
    ['Rosa', [255, 80, 150]], ['Blanco cálido', [255, 190, 120]], ['Blanco', [255, 255, 255]],
  ];

  function loadState() {
    let saved = {};
    try { saved = JSON.parse(localStorage.getItem(KEY)) || {}; } catch { /* ignore */ }
    return {
      ...structuredClone(DEFAULTS), ...saved,
      music: { ...DEFAULTS.music, ...(saved.music || {}) },
      screen: { ...DEFAULTS.screen, ...(saved.screen || {}) },
    };
  }
  const state = loadState();
  if (state.mode === 'music' || state.mode === 'screen') state.mode = 'color';
  let saveTimer;
  const save = () => {
    clearTimeout(saveTimer);
    saveTimer = setTimeout(() => localStorage.setItem(KEY, JSON.stringify(state)), 250);
  };

  // ------------------------------------------------------------- Toasts
  function toast(message, type = 'info', ms = 3800) {
    const el = document.createElement('div');
    el.className = `toast ${type}`;
    el.innerHTML = `<span>${message}</span>`;
    $('#toasts').append(el);
    setTimeout(() => { el.classList.add('out'); el.addEventListener('animationend', () => el.remove()); }, ms);
  }
  let lastNudge = 0;
  function nudge() {
    if (led.state === 'connecting' || led.state === 'reconnecting') return;
    if (Date.now() - lastNudge < 7000) return;
    lastNudge = Date.now();
    toast('Conecta las tiras para aplicar los cambios (botón <b>Conectar tiras</b>).', 'warn');
  }

  // ------------------------------------------------------------- Visual (escena)
  let output = state.color.slice();
  function applyGlow(rgb) {
    output = rgb;
    root.style.setProperty('--led', rgb.join(' '));
  }
  function applyIntensity() {
    root.style.setProperty('--glow', state.power ? (0.3 + 0.7 * (state.brightness / 100)).toFixed(3) : '0');
  }
  function setRangeFill(input) {
    const min = +input.min || 0, max = +input.max || 100;
    input.style.setProperty('--fill', `${((input.value - min) / (max - min)) * 100}%`);
  }
  const MODE_LABEL = { color: 'Color fijo', effect: 'Efecto', music: 'Modo música', screen: 'Sincronizado con pantalla' };
  function updateSceneMode() {
    let label = MODE_LABEL[state.mode] || 'Color fijo';
    if (state.mode === 'effect') {
      const fx = LedProtocol.EFFECTS.find((f) => f.id === state.effect);
      if (fx) label = fx.name;
    }
    $('#scene-mode').textContent = state.power ? label : 'Apagado';
  }

  // ------------------------------------------------------------- BLE
  const led = new LedController();
  led.setProfile(state.profile);

  const STATUS_TEXT = {
    unsupported: 'Bluetooth no disponible',
    disconnected: 'Desconectado',
    connecting: 'Conectando…',
    reconnecting: 'Reconectando…',
  };
  led.addEventListener('state', (e) => {
    const { state: s, name } = e.detail;
    $('#status-pill').dataset.state = s;
    $('#status-text').textContent = s === 'connected' ? `Conectado · ${name}` : STATUS_TEXT[s] || s;
    $('#btn-connect-label').textContent =
      s === 'connected' ? 'Desconectar' : s === 'connecting' || s === 'reconnecting' ? 'Cancelar' : 'Conectar tiras';
    $('#btn-connect').classList.toggle('is-active', s === 'connected');
    $('#btn-connect').disabled = s === 'unsupported';
    renderDeviceInfo();
    if (s === 'connected') {
      toast(`Conectado a <b>${name}</b>`, 'ok');
      if (state.powerOnStart) {
        state.power = true;
        $('#btn-power').setAttribute('aria-pressed', 'true');
        applyIntensity();
        updateSceneMode();
      }
      if (state.restoreOnConnect || state.powerOnStart) pushStateToDevice();
    }
    reportBackendState();
  });
  led.addEventListener('log', (e) => addLog(e.detail));

  let reportTimer;
  function reportBackendState() {
    clearTimeout(reportTimer);
    reportTimer = setTimeout(() => {
      fetch('/api/state', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          connected: led.connected,
          power: state.power,
          name: led.info?.name || '',
          keepInBackground: state.keepInBackground,
          // Los lee el ayudante sin ventana (LotusShutdown.exe) para encender al iniciar y apagar al apagar
          powerOnStart: state.powerOnStart,
          powerOffShutdown: state.powerOffShutdown,
        }),
      }).catch(() => {});
    }, 250);
  }

  function pushStateToDevice() {
    led.power(state.power);
    reportBackendState();
    if (!state.power) return;
    led.brightness(state.brightness);
    if (state.mode === 'effect' && state.effect != null) {
      led.effect(state.effect);
      led.speed(state.speed);
    } else if (state.mode === 'color') {
      led.color(...state.color);
    }
  }

  async function onConnectClick() {
    if (led.state === 'connected' || led.state === 'connecting' || led.state === 'reconnecting') {
      led.disconnect();
      return;
    }
    try {
      if (!(await led.bluetoothAvailable())) {
        toast('El Bluetooth del PC parece desactivado. Actívalo en Configuración de Windows.', 'err', 6000);
        return;
      }
      await led.request(state.showAll);
    } catch (err) {
      if (err.name === 'NotFoundError' && /cancel/i.test(err.message)) return; // el usuario cerró el selector
      if (err.name === 'NotFoundError') {
        toast('No se encontró la tira. Cierra LotusLamp X en el móvil y prueba «Mostrar todos los dispositivos» en Ajustes.', 'warn', 7000);
      } else {
        toast(`No se pudo conectar: ${err.message}`, 'err', 6000);
      }
    }
  }

  async function tryAutoConnect() {
    if (!state.autoConnect || !led.supported || !navigator.bluetooth.getDevices) return;
    const id = localStorage.getItem('lotusled.deviceId');
    if (!id) return;
    let device = null;
    try {
      const devices = await navigator.bluetooth.getDevices();
      device = devices.find((d) => d.id === id);
      if (!device) return;
      await led.connectKnown(device);
    } catch (err) {
      addLog({ kind: 'err', text: `Conexión automática fallida: ${err.message}`, time: new Date() });
      if (device && led.state !== 'connected') {
        // No nos rendimos: en cuanto la tira esté encendida y libre, se conecta sola
        toast(`Esperando a <b>${(device.name || 'la tira').trim()}</b>… se conectará sola cuando esté disponible.`, 'warn', 6000);
        led.keepTrying(device);
      } else {
        toast('No se encontró la tira guardada. Pulsa <b>Conectar tiras</b>.', 'warn');
      }
    }
  }

  // ------------------------------------------------------------- Acciones
  function setPower(on, send = true) {
    state.power = on;
    if (send && !led.power(on)) nudge();
    if (!on) {
      stopSyncModes();
    } else if (state.mode === 'effect' && state.effect != null) {
      const fx = LedProtocol.EFFECTS.find((f) => f.id === state.effect);
      if (fx) startFxPreview(fx);
    }
    $('#btn-power').setAttribute('aria-pressed', String(on));
    applyIntensity();
    updateSceneMode();
    save();
  }

  function setColor(rgb, from = null) {
    stopModes('color');
    state.mode = 'color';
    state.effect = null;
    state.color = rgb.map((v) => clamp(Math.round(v), 0, 255));
    if (!state.power) setPower(true);
    if (!led.color(...state.color)) nudge();
    applyGlow(state.color);
    syncColorUI(from);
    renderEffectsActive();
    updateSceneMode();
    save();
  }

  function setBrightness(v) {
    state.brightness = clamp(Math.round(v), 1, 100);
    if (!state.power) setPower(true);
    if (!led.brightness(state.brightness)) nudge();
    $('#brightness-value').textContent = `${state.brightness}%`;
    applyIntensity();
    save();
  }

  function selectEffect(fx) {
    stopModes('effect');
    state.mode = 'effect';
    state.effect = fx.id;
    if (!state.power) setPower(true);
    if (!led.effect(fx.id)) nudge();
    led.speed(state.speed);
    startFxPreview(fx);
    renderEffectsActive();
    renderSwatchesActive();
    updateSceneMode();
    save();
  }

  /** Detiene los modos que no sean `keep`. */
  function stopModes(keep) {
    if (keep !== 'effect') stopFxPreview();
    if (keep !== 'music' && audio.running) audio.stop(true);
    if (keep !== 'screen' && screen.running) screen.stop(true);
    renderMusicUI();
    renderScreenUI();
  }
  function stopSyncModes() {
    const was = audio.running || screen.running;
    if (audio.running) audio.stop(true);
    if (screen.running) screen.stop(true);
    if (was) state.mode = 'color';
    renderMusicUI();
    renderScreenUI();
  }
  function restoreStatic() {
    state.mode = 'color';
    led.color(...state.color);
    applyGlow(state.color);
    renderMusicUI();
    renderScreenUI();
    updateSceneMode();
    save();
  }

  // ------------------------------------------------------------- Vista previa de efectos
  let fxTimer = null;
  function fxColorAt(fx, t) {
    const cols = fx.colors, n = cols.length;
    const rate = 0.25 + (state.speed / 100) * 2.75; // ciclos por segundo
    const cyc = t * rate;
    const i = Math.floor(cyc) % n;
    if (fx.kind === 'jump') return cols[i];
    if (fx.kind === 'blink') return cyc % 1 < 0.5 ? cols[i] : [0, 0, 0];
    if (n === 1) {
      const k = 0.5 - 0.5 * Math.cos(2 * Math.PI * cyc * 0.5);
      return cols[0].map((c) => Math.round(c * k));
    }
    const slow = cyc * 0.5, j = Math.floor(slow) % n, f = slow % 1;
    const a = cols[j], b = cols[(j + 1) % n];
    return a.map((c, k) => Math.round(c + (b[k] - c) * f));
  }
  function startFxPreview(fx) {
    stopFxPreview();
    const t0 = performance.now();
    fxTimer = setInterval(() => applyGlow(fxColorAt(fx, (performance.now() - t0) / 1000)), 40);
  }
  function stopFxPreview() {
    if (fxTimer) { clearInterval(fxTimer); fxTimer = null; }
  }

  // ------------------------------------------------------------- Rueda de color
  class ColorWheel {
    constructor(canvas, thumb, onChange) {
      this.canvas = canvas;
      this.thumb = thumb;
      this.wrap = canvas.parentElement;
      this.onChange = onChange;
      this.h = 0; this.s = 1;
      this.size = 0;
      new ResizeObserver(() => this.draw()).observe(canvas);
      this.bind();
    }
    draw() {
      const size = Math.round(this.canvas.clientWidth * devicePixelRatio);
      if (!size || size === this.size) return;
      this.size = size;
      this.canvas.width = this.canvas.height = size;
      const ctx = this.canvas.getContext('2d');
      const img = ctx.createImageData(size, size);
      const d = img.data, R = size / 2;
      for (let y = 0; y < size; y++) {
        for (let x = 0; x < size; x++) {
          const dx = x - R + 0.5, dy = y - R + 0.5;
          const dist = Math.hypot(dx, dy);
          const i = (y * size + x) * 4;
          if (dist > R) continue;
          const h = (Math.atan2(dy, dx) * 180) / Math.PI + 90;
          const [r, g, b] = Color.hsvToRgb(h, Math.min(1, dist / R), 1);
          d[i] = r; d[i + 1] = g; d[i + 2] = b;
          d[i + 3] = dist > R - 1.5 ? Math.round((255 * (R - dist)) / 1.5) : 255;
        }
      }
      ctx.putImageData(img, 0, 0);
    }
    set(h, s) {
      this.h = h; this.s = s;
      const a = ((h - 90) * Math.PI) / 180;
      this.thumb.style.left = `${50 + Math.cos(a) * s * 50}%`;
      this.thumb.style.top = `${50 + Math.sin(a) * s * 50}%`;
    }
    bind() {
      const pick = (e) => {
        const rect = this.canvas.getBoundingClientRect();
        const R = rect.width / 2;
        const dx = e.clientX - rect.left - R, dy = e.clientY - rect.top - R;
        const s = Math.min(1, Math.hypot(dx, dy) / R);
        const h = ((Math.atan2(dy, dx) * 180) / Math.PI + 90 + 360) % 360;
        this.set(h, s);
        this.onChange(h, s);
      };
      this.canvas.addEventListener('pointerdown', (e) => {
        this.canvas.setPointerCapture(e.pointerId);
        this.wrap.classList.add('dragging');
        pick(e);
      });
      this.canvas.addEventListener('pointermove', (e) => { if (this.canvas.hasPointerCapture(e.pointerId)) pick(e); });
      const end = () => this.wrap.classList.remove('dragging');
      this.canvas.addEventListener('pointerup', end);
      this.canvas.addEventListener('pointercancel', end);
    }
  }

  const wheel = new ColorWheel($('#wheel'), $('#wheel-thumb'), (h, s) => setColor(Color.hsvToRgb(h, s, 1), 'wheel'));

  function syncColorUI(from) {
    const c = state.color;
    if (from !== 'wheel') {
      const [h, s] = Color.rgbToHsv(...c);
      wheel.set(h, s);
    }
    if (from !== 'hex') $('#hex-input').value = Color.toHex(c);
    $('#rgb-readout').textContent = `R ${c[0]} · G ${c[1]} · B ${c[2]}`;
    renderSwatchesActive();
  }

  // ------------------------------------------------------------- Swatches
  function makeSwatch(rgb, title) {
    const b = document.createElement('button');
    b.type = 'button';
    b.className = 'swatch';
    b.title = title;
    b.style.setProperty('--c', `rgb(${rgb.join(',')})`);
    b.dataset.rgb = rgb.join(',');
    b.addEventListener('click', () => setColor(rgb));
    return b;
  }
  function renderQuickColors() {
    const box = $('#quick-colors');
    box.replaceChildren(...QUICK_COLORS.map(([name, rgb]) => makeSwatch(rgb, name)));
  }
  function renderFavorites() {
    const box = $('#favorites');
    if (!state.favorites.length) {
      box.innerHTML = '<div class="swatch-empty">Aún no tienes favoritos.</div>';
      return;
    }
    box.replaceChildren(...state.favorites.map((rgb, i) => {
      const b = makeSwatch(rgb, `${Color.toHex(rgb)} — clic derecho para eliminar`);
      b.addEventListener('contextmenu', (e) => {
        e.preventDefault();
        state.favorites.splice(i, 1);
        renderFavorites();
        save();
      });
      return b;
    }));
    renderSwatchesActive();
  }
  function renderSwatchesActive() {
    const key = state.mode === 'color' ? state.color.join(',') : '';
    $$('.swatch').forEach((s) => s.classList.toggle('active', s.dataset.rgb === key));
  }

  // ------------------------------------------------------------- Efectos
  let fxFilter = 'all';
  function renderEffects() {
    const grid = $('#fx-grid');
    grid.replaceChildren(...LedProtocol.EFFECTS.map((fx) => {
      const card = document.createElement('button');
      card.type = 'button';
      card.className = 'fx-card';
      card.dataset.id = fx.id;
      card.dataset.kind = fx.kind;
      const prev = document.createElement('div');
      const css = fx.colors.map((c) => `rgb(${c.join(',')})`);
      const n = fx.colors.length;
      prev.className = 'fx-preview';
      if (n === 1) {
        prev.style.background = css[0];
        prev.classList.add(fx.kind === 'blink' ? 'blink' : 'breathe');
      } else {
        const stops = fx.kind === 'fade'
          ? [...css, css[0]].join(',')
          : css.map((c, i) => `${c} ${(i / n) * 100}% ${((i + 1) / n) * 100}%`).join(',');
        prev.style.backgroundImage = `linear-gradient(90deg, ${stops})`;
        prev.style.setProperty('--fx-size', `${n * 100}%`);
        prev.style.setProperty('--fx-steps', n - 1);
        prev.style.setProperty('--fx-dur', `${n * 0.8}s`);
        prev.classList.add(fx.kind);
        if (fx.kind === 'blink') prev.classList.add('multi');
      }
      card.style.setProperty('--fx-glow', css[0]);
      const name = document.createElement('span');
      name.className = 'fx-name';
      name.textContent = fx.name;
      card.append(prev, name);
      card.addEventListener('click', () => selectEffect(fx));
      return card;
    }));
    applyFxFilter();
    renderEffectsActive();
  }
  function applyFxFilter() {
    $$('.fx-card').forEach((c) => { c.hidden = fxFilter !== 'all' && c.dataset.kind !== fxFilter; });
    $$('#fx-filter .chip').forEach((c) => c.classList.toggle('active', c.dataset.filter === fxFilter));
  }
  function renderEffectsActive() {
    $$('.fx-card').forEach((c) => c.classList.toggle('active', state.mode === 'effect' && +c.dataset.id === state.effect));
  }

  // ------------------------------------------------------------- Música
  const vizCanvas = $('#viz');
  const vizCtx = vizCanvas.getContext('2d');
  const audio = new AudioSync({
    getBase: () => state.color,
    onColor: (rgb) => { applyGlow(rgb); led.color(...rgb); },
    onFrame: (bins) => drawViz(bins),
    onStop: () => { toast('Modo música detenido.'); restoreStatic(); },
  });

  function drawViz(bins) {
    const w = vizCanvas.clientWidth * devicePixelRatio, h = vizCanvas.clientHeight * devicePixelRatio;
    if (vizCanvas.width !== w || vizCanvas.height !== h) { vizCanvas.width = w; vizCanvas.height = h; }
    vizCtx.clearRect(0, 0, w, h);
    if (!bins) return;
    const bars = 56, gap = 3 * devicePixelRatio;
    const bw = (w - gap * (bars - 1)) / bars;
    const [r, g, b] = output;
    const grad = vizCtx.createLinearGradient(0, h, 0, 0);
    grad.addColorStop(0, `rgba(${r},${g},${b},0.25)`);
    grad.addColorStop(1, `rgba(${Math.min(255, r + 80)},${Math.min(255, g + 80)},${Math.min(255, b + 80)},1)`);
    vizCtx.fillStyle = grad;
    const n = bins.length, minF = 1, maxF = n * 0.45;
    for (let i = 0; i < bars; i++) {
      const a = Math.floor(minF * (maxF / minF) ** (i / bars));
      const z = Math.max(a + 1, Math.floor(minF * (maxF / minF) ** ((i + 1) / bars)));
      let sum = 0;
      for (let k = a; k < z; k++) sum += bins[k];
      const v = sum / (z - a) / 255;
      const bh = Math.max(2 * devicePixelRatio, v ** 1.3 * h * 0.92);
      const x = i * (bw + gap);
      vizCtx.beginPath();
      vizCtx.roundRect(x, h - bh, bw, bh, Math.min(bw / 2, 4 * devicePixelRatio));
      vizCtx.fill();
    }
  }

  async function toggleMusic() {
    if (audio.running) {
      audio.stop(true);
      restoreStatic();
      drawViz(null);
      return;
    }
    stopModes('music');
    if (!state.power) setPower(true);
    if (!led.connected) nudge();
    try {
      await audio.start(state.music.source, state.music);
      state.mode = 'music';
      toast('Modo música activado 🎵', 'ok');
    } catch (err) {
      mediaError(err);
    }
    renderMusicUI();
    renderEffectsActive();
    renderSwatchesActive();
    updateSceneMode();
  }

  function renderMusicUI() {
    const on = audio.running;
    const btn = $('#btn-music');
    btn.classList.toggle('is-active', on);
    btn.querySelector('span').textContent = on ? 'Detener modo música' : 'Iniciar modo música';
    btn.querySelector('svg').innerHTML = on ? '<rect x="6" y="6" width="12" height="12" rx="2"/>' : '<path d="M6 4l14 8-14 8z"/>';
    $('.viz-card').classList.toggle('live', on);
    $$('#music-source button').forEach((b) => b.classList.toggle('active', b.dataset.value === state.music.source));
    $$('#music-mode button').forEach((b) => b.classList.toggle('active', b.dataset.value === state.music.mode));
    $('#music-source-hint').innerHTML = state.music.source === 'system'
      ? 'Al iniciar, elige <b>Pantalla completa</b> y activa <b>«Compartir también el audio del sistema»</b>.'
      : 'Usa el micrófono del PC: reacciona al sonido de la habitación.';
  }

  // ------------------------------------------------------------- Pantalla
  const screen = new ScreenSync({
    video: $('#screen-video'),
    onColor: (rgb) => { applyGlow(rgb); led.color(...rgb); },
    onStop: () => { toast('Sincronización con pantalla detenida.'); restoreStatic(); },
  });

  async function toggleScreen() {
    if (screen.running) {
      screen.stop(true);
      restoreStatic();
      return;
    }
    stopModes('screen');
    if (!state.power) setPower(true);
    if (!led.connected) nudge();
    try {
      await screen.start(state.screen);
      state.mode = 'screen';
      toast('Sincronización con pantalla activada. Puedes minimizar esta ventana.', 'ok', 5000);
    } catch (err) {
      mediaError(err);
    }
    renderScreenUI();
    renderEffectsActive();
    renderSwatchesActive();
    updateSceneMode();
  }

  function renderScreenUI() {
    const on = screen.running;
    const btn = $('#btn-screen');
    btn.classList.toggle('is-active', on);
    btn.querySelector('span').textContent = on ? 'Detener sincronización' : 'Sincronizar con la pantalla';
    btn.querySelector('svg').innerHTML = on ? '<rect x="6" y="6" width="12" height="12" rx="2"/>' : '<path d="M6 4l14 8-14 8z"/>';
    $('.screen-preview').classList.toggle('live', on);
    $('#region-overlay').dataset.region = state.screen.region;
    $('#region-overlay').style.opacity = on ? 1 : 0.35;
    $$('#screen-region .chip').forEach((b) => b.classList.toggle('active', b.dataset.value === state.screen.region));
    $$('#screen-algo button').forEach((b) => b.classList.toggle('active', b.dataset.value === state.screen.algo));
    const mon = $('#monitor-video');
    mon.srcObject = on ? screen.stream : null;
    if (on) mon.play().catch(() => {});
    mon.parentElement.classList.toggle('live', on);
  }

  function mediaError(err) {
    if (err.name === 'NotAllowedError') toast('Permiso denegado o selección cancelada.', 'warn');
    else if (err.name === 'NotFoundError') toast('No se encontró ningún micrófono.', 'err');
    else toast(err.message || String(err), 'err', 7000);
  }

  // ------------------------------------------------------------- Ajustes
  function renderDeviceInfo() {
    const i = led.info;
    const remembered = localStorage.getItem('lotusled.deviceName');
    $('#info-name').textContent = i?.name || (remembered ? `${remembered} (guardado)` : '—');
    $('#info-char').textContent = i ? `${i.service.toUpperCase()} / ${i.characteristic.toUpperCase()}` : '—';
    $('#info-write').textContent = i?.writeMode || '—';
    $('#info-profile').textContent = i ? i.profile.toUpperCase() : '—';
  }

  const logLines = [];
  function addLog({ kind, text, time }) {
    const el = document.createElement('div');
    el.className = `log-line ${kind}`;
    const t = time.toLocaleTimeString('es', { hour12: false });
    el.innerHTML = `<time>${t}</time><span></span>`;
    el.lastChild.textContent = (kind === 'tx' ? '→ ' : '') + text;
    const box = $('#log');
    box.prepend(el);
    logLines.unshift(el);
    while (logLines.length > 120) logLines.pop().remove();
  }

  let sleepEnd = 0, sleepTimer = null;
  function setSleep(minutes) {
    clearInterval(sleepTimer);
    sleepEnd = minutes ? Date.now() + minutes * 60000 : 0;
    if (minutes) {
      sleepTimer = setInterval(() => {
        if (Date.now() >= sleepEnd) {
          clearInterval(sleepTimer);
          sleepEnd = 0;
          setPower(false);
          $('#sleep-select').value = '0';
          toast('Temporizador: tiras apagadas. Buenas noches 🌙');
        }
        renderSleep();
      }, 1000);
      toast(`Las tiras se apagarán en ${minutes} min.`);
    }
    renderSleep();
  }
  function renderSleep() {
    const chip = $('#sleep-chip');
    if (!sleepEnd) { chip.hidden = true; return; }
    const left = Math.max(0, Math.round((sleepEnd - Date.now()) / 1000));
    const m = Math.floor(left / 60), s = left % 60;
    $('#sleep-chip-text').textContent = `${m}:${String(s).padStart(2, '0')}`;
    chip.hidden = false;
  }

  // ------------------------------------------------------------- Pestañas
  function selectTab(name) {
    state.tab = name;
    $$('.tab').forEach((t) => {
      const on = t.dataset.tab === name;
      t.classList.toggle('active', on);
      t.setAttribute('aria-selected', String(on));
    });
    $$('.tab-panel').forEach((p) => p.classList.toggle('active', p.id === `tab-${name}`));
    moveIndicator();
    if (name === 'music') requestAnimationFrame(() => drawViz(null));
    save();
  }
  function moveIndicator(instant = false) {
    const active = $('.tab.active');
    if (!active) return;
    const ind = $('#tab-indicator');
    if (instant) ind.style.transition = 'none';
    ind.style.left = `${active.offsetLeft}px`;
    ind.style.width = `${active.offsetWidth}px`;
    if (instant) { void ind.offsetWidth; ind.style.transition = ''; }
  }

  // ------------------------------------------------------------- Enlaces de eventos
  function bindRange(id, onInput, fmt) {
    const el = $(`#${id}`);
    const out = $(`#${id}-value`);
    el.addEventListener('input', () => {
      setRangeFill(el);
      if (out && fmt) out.textContent = fmt(+el.value);
      onInput(+el.value);
    });
    return el;
  }

  function bindEvents() {
    $('#btn-connect').addEventListener('click', onConnectClick);
    $('#btn-power').addEventListener('click', () => setPower(!state.power));

    bindRange('brightness', setBrightness, (v) => `${v}%`);
    bindRange('speed', (v) => { state.speed = v; led.speed(v); save(); }, (v) => `${v}%`);
    bindRange('kelvin', (k) => setColor(Color.kelvin(k)), (v) => `${v} K`);

    $('#hex-input').addEventListener('input', (e) => {
      const rgb = Color.fromHex(e.target.value);
      if (rgb && e.target.value.replace('#', '').length === 6) setColor(rgb, 'hex');
    });
    $('#hex-input').addEventListener('blur', (e) => { e.target.value = Color.toHex(state.color); });

    $('#btn-add-fav').addEventListener('click', () => {
      if (state.favorites.some((f) => Color.same(f, state.color))) { toast('Ese color ya está en favoritos.'); return; }
      state.favorites.push(state.color.slice());
      if (state.favorites.length > 18) state.favorites.shift();
      renderFavorites();
      save();
      toast(`Guardado ${Color.toHex(state.color)} en favoritos`, 'ok');
    });

    $$('.tab').forEach((t) => t.addEventListener('click', () => selectTab(t.dataset.tab)));
    if ('ResizeObserver' in window) new ResizeObserver(() => moveIndicator(true)).observe($('.tabs'));
    else window.addEventListener('resize', () => moveIndicator(true));

    $$('#fx-filter .chip').forEach((c) => c.addEventListener('click', () => { fxFilter = c.dataset.filter; applyFxFilter(); }));

    $$('#music-source button').forEach((b) => b.addEventListener('click', async () => {
      if (state.music.source === b.dataset.value) return;
      state.music.source = b.dataset.value;
      save();
      if (audio.running) { audio.stop(true); renderMusicUI(); await toggleMusic(); } else renderMusicUI();
    }));
    $$('#music-mode button').forEach((b) => b.addEventListener('click', () => {
      state.music.mode = b.dataset.value; renderMusicUI(); save();
    }));
    bindRange('music-sensitivity', (v) => { state.music.sensitivity = v; save(); }, (v) => `${v}%`);
    bindRange('music-floor', (v) => { state.music.floor = v; save(); }, (v) => `${v}%`);
    $('#btn-music').addEventListener('click', toggleMusic);

    $$('#screen-region .chip').forEach((b) => b.addEventListener('click', () => {
      state.screen.region = b.dataset.value; renderScreenUI(); save();
    }));
    $$('#screen-algo button').forEach((b) => b.addEventListener('click', () => {
      state.screen.algo = b.dataset.value; renderScreenUI(); save();
    }));
    bindRange('screen-smoothing', (v) => { state.screen.smoothing = v; save(); }, (v) => `${v}%`);
    bindRange('screen-saturation', (v) => { state.screen.saturation = v; save(); }, (v) => `${v}%`);
    $('#btn-screen').addEventListener('click', toggleScreen);

    $('#profile-select').addEventListener('change', (e) => {
      state.profile = e.target.value;
      led.setProfile(state.profile);
      renderDeviceInfo();
      save();
      if (led.connected) { pushStateToDevice(); toast(`Protocolo: ${e.target.selectedOptions[0].text}`); }
    });
    const bindToggle = (id, key, onChange) => {
      const el = $(`#${id}`);
      if (!el) return;
      el.checked = !!state[key];
      el.addEventListener('change', () => {
        state[key] = el.checked;
        save();
        if (onChange) onChange(el.checked);
      });
    };
    bindToggle('opt-power-on-start', 'powerOnStart', reportBackendState);
    bindToggle('opt-power-off-shutdown', 'powerOffShutdown', reportBackendState);
    bindToggle('opt-autoconnect', 'autoConnect');
    bindToggle('opt-restore', 'restoreOnConnect');
    bindToggle('opt-showall', 'showAll');

    // Autostart con Windows
    const autostartEl = $('#opt-autostart');
    if (autostartEl) {
      autostartEl.checked = !!state.autostart;
      autostartEl.addEventListener('change', async (e) => {
        state.autostart = e.target.checked;
        save();
        try {
          const res = await fetch('/api/autostart', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ enabled: e.target.checked }),
          });
          if (res.ok) {
            toast(e.target.checked ? 'Al iniciar Windows se encenderán las luces, sin abrir la ventana' : 'Inicio automático desactivado', 'ok');
          }
        } catch {
          toast('No se pudo comunicar con el lanzador de Windows.', 'warn');
        }
      });
    }

    $('#sleep-select').addEventListener('change', (e) => setSleep(+e.target.value));
    $('#btn-forget').addEventListener('click', async () => {
      await led.forget();
      renderDeviceInfo();
      toast('Dispositivo olvidado. La próxima vez tendrás que buscarlo de nuevo.');
    });
    $('#raw-form').addEventListener('submit', (e) => {
      e.preventDefault();
      const bytes = LedProtocol.parseHex($('#raw-input').value);
      if (!bytes) { toast('Formato HEX no válido. Ejemplo: 7E 00 05 03 FF 00 00 00 EF', 'err'); return; }
      if (!led.raw(bytes)) nudge();
    });

    document.addEventListener('keydown', (e) => {
      if (e.code !== 'Space' || e.repeat) return;
      if (e.target.closest('input, select, textarea, button')) return;
      e.preventDefault();
      setPower(!state.power);
    });
  }

  // ------------------------------------------------------------- Comunicación en segundo plano
  async function syncAutostartStatus() {
    try {
      const res = await fetch('/api/autostart');
      if (res.ok) {
        const data = await res.json();
        const el = $('#opt-autostart');
        if (el && typeof data.autostart === 'boolean') {
          el.checked = data.autostart;
          state.autostart = data.autostart;
          save();
        }
      }
    } catch { /* Sin servidor backend local */ }
  }

  async function startBackendPoll() {
    while (true) {
      try {
        const res = await fetch('/api/poll');
        if (res.ok) {
          const msg = await res.json();
          if (msg.event === 'shutdown') {
            // Windows se apaga. LotusShutdown.exe se encarga de apagar la tira; aquí paramos la música o el
            // ambilight (si no, seguirían mandando colores) y la apagamos también por esta conexión.
            if (state.powerOffShutdown) {
              stopSyncModes();
              if (led.connected) {
                led.power(false);
                await led.flush(1500);
              }
            }
            fetch('/api/shutdown-ack', { method: 'POST' }).catch(() => {});
            // Si dentro de un rato seguimos aquí, el apagado se canceló: seguimos atendiendo al lanzador
            await new Promise((r) => setTimeout(r, 30000));
            continue;
          } else if (msg.event === 'set-power') {
            setPower(!!msg.power);
          }
        }
      } catch {
        await new Promise((r) => setTimeout(r, 2500));
      }
    }
  }

  // ------------------------------------------------------------- Inicio
  function init() {
    if (!led.supported) {
      $('#unsupported-banner').hidden = false;
      $('#status-pill').dataset.state = 'unsupported';
      $('#status-text').textContent = STATUS_TEXT.unsupported;
      $('#btn-connect').disabled = true;
    }

    const isStartupLaunch = location.hash.includes('startup');
    if (isStartupLaunch || state.powerOnStart) {
      state.power = true;
    }

    renderQuickColors();
    renderFavorites();
    renderEffects();
    bindEvents();

    // Valores iniciales de controles
    const setVal = (id, v) => { const el = $(`#${id}`); if (el) { el.value = v; setRangeFill(el); } };
    setVal('brightness', state.brightness); $('#brightness-value').textContent = `${state.brightness}%`;
    setVal('speed', state.speed); $('#speed-value').textContent = `${state.speed}%`;
    setVal('kelvin', 4000);
    setVal('music-sensitivity', state.music.sensitivity); $('#music-sensitivity-value').textContent = `${state.music.sensitivity}%`;
    setVal('music-floor', state.music.floor); $('#music-floor-value').textContent = `${state.music.floor}%`;
    setVal('screen-smoothing', state.screen.smoothing); $('#screen-smoothing-value').textContent = `${state.screen.smoothing}%`;
    setVal('screen-saturation', state.screen.saturation); $('#screen-saturation-value').textContent = `${state.screen.saturation}%`;
    $('#profile-select').value = state.profile;

    applyGlow(state.color);
    syncColorUI();
    $('#btn-power').setAttribute('aria-pressed', String(state.power));
    applyIntensity();
    if (state.mode === 'effect' && state.effect != null && state.power) {
      const fx = LedProtocol.EFFECTS.find((f) => f.id === state.effect);
      if (fx) startFxPreview(fx); else state.mode = 'color';
    }
    renderMusicUI();
    renderScreenUI();
    renderDeviceInfo();
    updateSceneMode();
    const hashTab = decodeURIComponent(location.hash.slice(1)).replace(/^startup$/, '');
    const validHash = hashTab && document.querySelector(`.tab[data-tab="${CSS.escape(hashTab)}"]`);
    selectTab(validHash ? hashTab : (state.tab || 'color'));
    moveIndicator(true);
    document.fonts?.ready.then(() => moveIndicator(true));

    addLog({ kind: 'info', text: 'LotusLED Desk listo.', time: new Date() });
    tryAutoConnect();
    syncAutostartStatus();
    startBackendPoll();
    reportBackendState();
  }

  init();
})();
