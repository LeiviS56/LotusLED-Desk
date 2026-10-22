'use strict';
/**
 * LotusLED Desk — capa Bluetooth LE.
 *
 * Las tiras que usan la app "LotusLamp X" llevan casi siempre un controlador
 * ELK-BLEDOM / MELK (servicio 0xFFF0, característica de escritura 0xFFF3).
 * Los comandos son tramas de 9 bytes que empiezan por 0x7E y terminan en 0xEF.
 */
(function (global) {
  const full = (short) => `0000${short}-0000-1000-8000-00805f9b34fb`;
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const shortUuid = (u) => (/^0000([0-9a-f]{4})-0000-1000-8000-00805f9b34fb$/i.exec(u) || [])[1] || u;

  function withTimeout(promise, ms, message) {
    let t;
    return Promise.race([
      promise.finally(() => clearTimeout(t)),
      new Promise((_, rej) => { t = setTimeout(() => rej(new Error(message)), ms); }),
    ]);
  }

  /** Servicios a los que pedimos acceso (Web Bluetooth exige declararlos). */
  const SERVICES = ['fff0', 'ffe0', 'ffe5', 'ffd0', 'ffd5', 'ff00', 'ae00', 'fee7'].map(full);
  /** Características de escritura, en orden de preferencia. */
  const PREFERRED_CHARS = ['fff3', 'ffe1', 'ffe9', 'ffd9', 'ff01', 'ae01', 'fff2', 'fff1'].map(full);
  /** Prefijos de nombre habituales de estos controladores. */
  const NAME_PREFIXES = ['L7', 'L7179', 'ELK', 'MELK', 'BLEDOM', 'LEDBLE', 'BLE-LED', 'LED', 'Lotus', 'QHM', 'BJ_LED', 'Triones', 'LEDnet'];

  // ---------------------------------------------------------------- Protocolo
  const R = [255, 0, 0], G = [0, 255, 0], B = [0, 0, 255], Y = [255, 255, 0];
  const C = [0, 255, 255], M = [255, 0, 255], W = [255, 255, 255];
  const SEVEN = [R, G, B, Y, C, M, W];

  const EFFECTS = [
    { id: 0x80, name: 'Salto RGB', kind: 'jump', colors: [R, G, B] },
    { id: 0x81, name: 'Salto 7 colores', kind: 'jump', colors: SEVEN },
    { id: 0x82, name: 'Respiración roja', kind: 'fade', colors: [R] },
    { id: 0x83, name: 'Respiración verde', kind: 'fade', colors: [G] },
    { id: 0x84, name: 'Respiración azul', kind: 'fade', colors: [B] },
    { id: 0x85, name: 'Respiración amarilla', kind: 'fade', colors: [Y] },
    { id: 0x86, name: 'Respiración cian', kind: 'fade', colors: [C] },
    { id: 0x87, name: 'Respiración magenta', kind: 'fade', colors: [M] },
    { id: 0x88, name: 'Respiración blanca', kind: 'fade', colors: [W] },
    { id: 0x89, name: 'Fundido rojo · verde', kind: 'fade', colors: [R, G] },
    { id: 0x8a, name: 'Fundido rojo · azul', kind: 'fade', colors: [R, B] },
    { id: 0x8b, name: 'Fundido verde · azul', kind: 'fade', colors: [G, B] },
    { id: 0x8c, name: 'Fundido RGB', kind: 'fade', colors: [R, G, B] },
    { id: 0x8d, name: 'Fundido 7 colores', kind: 'fade', colors: SEVEN },
    { id: 0x8e, name: 'Destello rojo', kind: 'blink', colors: [R] },
    { id: 0x8f, name: 'Destello verde', kind: 'blink', colors: [G] },
    { id: 0x90, name: 'Destello azul', kind: 'blink', colors: [B] },
    { id: 0x91, name: 'Destello amarillo', kind: 'blink', colors: [Y] },
    { id: 0x92, name: 'Destello cian', kind: 'blink', colors: [C] },
    { id: 0x93, name: 'Destello magenta', kind: 'blink', colors: [M] },
    { id: 0x94, name: 'Destello blanco', kind: 'blink', colors: [W] },
    { id: 0x95, name: 'Destello 7 colores', kind: 'blink', colors: SEVEN },
  ];

  const Protocol = {
    EFFECTS,
    /** 'melk' para controladores MELK-xxxx, 'elk' para ELK-BLEDOM y compatibles. */
    detectProfile(name) {
      return /^melk/i.test(name || '') ? 'melk' : 'elk';
    },
    init(profile) {
      return profile === 'melk' ? [[0x7e, 0x07, 0x83], [0x7e, 0x04, 0x04]] : [];
    },
    power(on, profile) {
      if (profile === 'melk') {
        return on ? [0x7e, 0x07, 0x04, 0xff, 0x00, 0x01, 0x02, 0x01, 0xef]
                  : [0x7e, 0x07, 0x04, 0x00, 0x00, 0x00, 0x02, 0x01, 0xef];
      }
      return on ? [0x7e, 0x00, 0x04, 0xf0, 0x00, 0x01, 0xff, 0x00, 0xef]
                : [0x7e, 0x00, 0x04, 0x00, 0x00, 0x00, 0xff, 0x00, 0xef];
    },
    color(r, g, b, profile) {
      return profile === 'melk' ? [0x7e, 0x07, 0x05, 0x03, r, g, b, 0x10, 0xef]
                                : [0x7e, 0x00, 0x05, 0x03, r, g, b, 0x00, 0xef];
    },
    brightness(pct) {
      return [0x7e, 0x00, 0x01, clamp(Math.round(pct), 0, 100), 0x00, 0x00, 0x00, 0x00, 0xef];
    },
    effect(id) {
      return [0x7e, 0x00, 0x03, id, 0x03, 0x00, 0x00, 0x00, 0xef];
    },
    speed(pct) {
      return [0x7e, 0x00, 0x02, clamp(Math.round(pct), 0, 100), 0x00, 0x00, 0x00, 0x00, 0xef];
    },
    parseHex(text) {
      const clean = String(text).replace(/0x/gi, '').replace(/[^0-9a-f]/gi, '');
      if (!clean.length || clean.length % 2) return null;
      return clean.match(/../g).map((h) => parseInt(h, 16));
    },
    toHex(bytes) {
      return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join(' ').toUpperCase();
    },
  };

  // ---------------------------------------------------------------- Controlador
  class LedController extends EventTarget {
    constructor() {
      super();
      this.device = null;
      this.server = null;
      this.char = null;
      this.noResponse = true;
      this.profileSetting = 'auto';
      this.profile = 'elk';
      this.state = this.supported ? 'disconnected' : 'unsupported';
      this.info = null;
      this.minGap = 12;
      this._pending = new Map();
      this._busy = false;
      this._manual = false;
      this._connecting = false;
      this._looping = false;
      this._watchAbort = null;
      this._lastTxLog = 0;
      this._onDisconnected = this._onDisconnected.bind(this);
    }

    get supported() { return typeof navigator !== 'undefined' && !!navigator.bluetooth; }
    get connected() { return this.state === 'connected' && !!this.char; }

    _emit(type, detail) { this.dispatchEvent(new CustomEvent(type, { detail })); }
    _setState(state, extra = {}) {
      this.state = state;
      this._emit('state', { state, name: this.device?.name || 'Tira LED', ...extra });
    }
    log(kind, text) { this._emit('log', { kind, text, time: new Date() }); }

    async bluetoothAvailable() {
      try { return this.supported && (await navigator.bluetooth.getAvailability()); } catch { return true; }
    }

    /** Abre el selector de dispositivos del navegador y conecta. */
    async request(showAll = false) {
      if (!this.supported) throw new Error('Este navegador no soporta Web Bluetooth.');
      const options = showAll
        ? { acceptAllDevices: true, optionalServices: SERVICES }
        : {
            filters: [
              ...NAME_PREFIXES.map((namePrefix) => ({ namePrefix })),
              ...['fff0', 'ffe0', 'ffd5'].map((s) => ({ services: [full(s)] })),
            ],
            optionalServices: SERVICES,
          };
      const device = await navigator.bluetooth.requestDevice(options);
      await this.connect(device);
      return device;
    }

    /** Intenta reconectar con un dispositivo ya autorizado (sin selector). */
    async connectKnown(device, attempts = 2) {
      if (typeof device.watchAdvertisements === 'function') {
        this._setState('connecting');
        const ac = new AbortController();
        const seen = new Promise((res) => device.addEventListener('advertisementreceived', () => res(true), { once: true }));
        try {
          await device.watchAdvertisements({ signal: ac.signal });
          await Promise.race([seen, sleep(8000)]);
        } catch { /* sin soporte: probamos a conectar igualmente */ }
        finally { ac.abort(); }
      }
      return this.connect(device, attempts);
    }

    async connect(device, attempts = 3) {
      if (this.device && this.device !== device) {
        this.device.removeEventListener('gattserverdisconnected', this._onDisconnected);
        try { this.device.gatt.disconnect(); } catch { /* ignore */ }
      }
      this.device = device;
      this._manual = false;
      this._connecting = true;
      this.char = null;
      device.removeEventListener('gattserverdisconnected', this._onDisconnected);
      device.addEventListener('gattserverdisconnected', this._onDisconnected);
      this._setState('connecting');
      this.log('info', `Conectando a ${device.name || 'dispositivo'}…`);

      let lastError = null;
      try {
        for (let attempt = 1; attempt <= attempts && !this._manual; attempt++) {
          try {
            this.server = await withTimeout(device.gatt.connect(), 12000, 'Tiempo de conexión agotado');
            await sleep(200);
            this.char = await this._findWritable(this.server);
            break;
          } catch (err) {
            lastError = err;
            this.log('err', `Intento ${attempt}/${attempts}: ${err.message}`);
            try { device.gatt.disconnect(); } catch { /* ignore */ }
            await sleep(600 * attempt);
          }
        }
      } finally {
        this._connecting = false;
      }

      if (!this.char) {
        this._setState('disconnected');
        throw lastError || new Error('No se pudo conectar con la tira.');
      }

      this.noResponse = !!this.char.properties.writeWithoutResponse;
      this.profile = this.profileSetting === 'auto' ? Protocol.detectProfile(device.name) : this.profileSetting;
      this.info = {
        name: device.name || 'Sin nombre',
        id: device.id,
        service: shortUuid(this.char.service.uuid),
        characteristic: shortUuid(this.char.uuid),
        writeMode: this.noResponse ? 'Sin respuesta (rápido)' : 'Con respuesta',
        profile: this.profile,
      };
      try {
        localStorage.setItem('lotusled.deviceId', device.id);
        localStorage.setItem('lotusled.deviceName', device.name || '');
      } catch { /* ignore */ }
      this.log('ok', `Conectado a ${this.info.name} (perfil ${this.profile.toUpperCase()})`);
      this._setState('connected');
      for (const cmd of Protocol.init(this.profile)) this.send(null, cmd);
      return device;
    }

    async _findWritable(server) {
      const services = [];
      for (const uuid of SERVICES) {
        try { services.push(await server.getPrimaryService(uuid)); } catch { /* no existe */ }
      }
      if (!services.length) {
        try { services.push(...(await server.getPrimaryServices())); } catch { /* ignore */ }
      }
      if (!services.length) throw new Error('El dispositivo no expone ningún servicio LED conocido');

      const candidates = [];
      for (const svc of services) {
        let chars = [];
        try { chars = await svc.getCharacteristics(); } catch { continue; }
        for (const ch of chars) {
          if (ch.properties.write || ch.properties.writeWithoutResponse) candidates.push(ch);
        }
      }
      if (!candidates.length) throw new Error('No se encontró ninguna característica de escritura');
      const rank = (ch) => { const i = PREFERRED_CHARS.indexOf(ch.uuid.toLowerCase()); return i < 0 ? 99 : i; };
      candidates.sort((a, b) => rank(a) - rank(b));
      const best = candidates[0];
      this.log('info', `Servicio ${shortUuid(best.service.uuid)} · característica ${shortUuid(best.uuid)}`);
      return best;
    }

    _onDisconnected() {
      if (this._connecting) return;
      this.char = null;
      this.server = null;
      this._pending.clear();
      if (this._manual) {
        this.log('info', 'Desconectado');
        this._setState('disconnected');
        return;
      }
      this.log('err', 'Se perdió la conexión. Reintentando…');
      this._reconnectLoop();
    }

    /**
     * Espera a que la tira vuelva a anunciarse (encendida y libre). Si el navegador no puede
     * vigilar anuncios, simplemente espera `fallbackMs`.
     */
    async _waitForAdvertisement(device, fallbackMs) {
      if (typeof device.watchAdvertisements !== 'function') return sleep(fallbackMs);
      const ac = new AbortController();
      this._watchAbort = ac;
      try {
        const seen = new Promise((res) => device.addEventListener('advertisementreceived', () => res(true), { once: true }));
        const aborted = new Promise((res) => ac.signal.addEventListener('abort', () => res(false), { once: true }));
        await device.watchAdvertisements({ signal: ac.signal });
        // Cada cierto tiempo se intenta igualmente, por si el aviso no llega
        await Promise.race([seen, aborted, sleep(Math.max(fallbackMs, 30000))]);
      } catch {
        await sleep(fallbackMs);
      } finally {
        ac.abort();
        if (this._watchAbort === ac) this._watchAbort = null;
      }
    }

    /**
     * Sigue intentando reconectar sin rendirse (como hace la app del móvil): unos reintentos
     * rápidos y después espera a que la tira vuelva a aparecer. Se para con «Desconectar».
     */
    async _reconnectLoop() {
      const device = this.device;
      if (this._looping) return;
      this._looping = true;
      try {
        for (let i = 1; ; i++) {
          if (this._manual || this.device !== device) return;
          this._setState('reconnecting', { attempt: i });
          if (i <= 3) await sleep(1000 * i);
          else await this._waitForAdvertisement(device, 10000);
          if (this._manual || this.device !== device) return;
          try {
            await this.connect(device, 1);
            this._emit('reconnected');
            return;
          } catch {
            if (i === 3) this.log('info', 'La tira no responde. Se conectará sola en cuanto vuelva a estar disponible.');
          }
        }
      } finally {
        this._looping = false;
      }
    }

    /** Para la conexión automática al abrir: si la tira guardada no está, se queda esperándola. */
    keepTrying(device) {
      this.device = device;
      this._manual = false;
      this._reconnectLoop();
    }

    disconnect() {
      this._manual = true;
      if (this._watchAbort) this._watchAbort.abort();
      this._pending.clear();
      if (this.device?.gatt?.connected) {
        this.device.gatt.disconnect();
      } else {
        this.char = null;
        this._setState('disconnected');
      }
    }

    async forget() {
      this.disconnect();
      try {
        localStorage.removeItem('lotusled.deviceId');
        localStorage.removeItem('lotusled.deviceName');
      } catch { /* ignore */ }
      if (this.device && typeof this.device.forget === 'function') {
        try { await this.device.forget(); } catch { /* ignore */ }
      }
      this.device = null;
      this.info = null;
    }

    // ------------------------------------------------------------ Cola de envío
    /**
     * Encola un comando. Si `key` ya estaba pendiente se sustituye por el más
     * reciente (así arrastrar la rueda de color no satura el Bluetooth).
     */
    send(key, bytes) {
      if (!this.char) return false;
      const k = key ?? Symbol('cmd');
      this._pending.delete(k);
      this._pending.set(k, Uint8Array.from(bytes));
      this._pump();
      return true;
    }

    async _pump() {
      if (this._busy) return;
      this._busy = true;
      try {
        while (this._pending.size && this.char) {
          const [key, data] = this._pending.entries().next().value;
          this._pending.delete(key);
          try {
            await this._write(data);
            const now = performance.now();
            if (typeof key === 'symbol' || now - this._lastTxLog > 400) {
              this._lastTxLog = now;
              this.log('tx', Protocol.toHex(data));
            }
          } catch (err) {
            this.log('err', `Error al enviar: ${err.message}`);
            if (!this.device?.gatt?.connected) break;
          }
          await sleep(this.minGap);
        }
      } finally {
        this._busy = false;
      }
    }

    async _write(data) {
      const ch = this.char;
      if (this.noResponse && ch.writeValueWithoutResponse) {
        try { return await ch.writeValueWithoutResponse(data); }
        catch (err) {
          if (err.name !== 'NotSupportedError') throw err;
          this.noResponse = false;
        }
      }
      return ch.writeValueWithResponse ? ch.writeValueWithResponse(data) : ch.writeValue(data);
    }

    // ------------------------------------------------------------ API de alto nivel
    power(on) { return this.send('power', Protocol.power(on, this.profile)); }
    color(r, g, b) { return this.send('color', Protocol.color(r, g, b, this.profile)); }
    brightness(pct) { return this.send('brightness', Protocol.brightness(pct)); }
    effect(id) { this._pending.delete('color'); return this.send('effect', Protocol.effect(id)); }
    speed(pct) { return this.send('speed', Protocol.speed(pct)); }
    raw(bytes) { return this.send(null, bytes); }
    setProfile(setting) {
      this.profileSetting = setting;
      if (this.device) this.profile = setting === 'auto' ? Protocol.detectProfile(this.device.name) : setting;
      if (this.info) this.info.profile = this.profile;
    }
  }

  global.LedProtocol = Protocol;
  global.LedController = LedController;
})(window);
