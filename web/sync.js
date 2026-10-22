'use strict';
/**
 * LotusLED Desk — modos sincronizados:
 *  - AudioSync: las luces reaccionan a la música (micrófono o audio del PC).
 *  - ScreenSync: las luces copian el color dominante de la pantalla (ambilight).
 */
(function (global) {
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));

  function hsvToRgb(h, s, v) {
    h = ((h % 360) + 360) % 360;
    const c = v * s, x = c * (1 - Math.abs(((h / 60) % 2) - 1)), m = v - c;
    let r, g, b;
    if (h < 60) [r, g, b] = [c, x, 0];
    else if (h < 120) [r, g, b] = [x, c, 0];
    else if (h < 180) [r, g, b] = [0, c, x];
    else if (h < 240) [r, g, b] = [0, x, c];
    else if (h < 300) [r, g, b] = [x, 0, c];
    else [r, g, b] = [c, 0, x];
    return [(r + m) * 255, (g + m) * 255, (b + m) * 255];
  }

  function rgbToHsv(r, g, b) {
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
  }

  const differs = (a, b, tol = 2) => !a || a.some((v, i) => Math.abs(v - b[i]) > tol);

  // ======================================================================
  // Música
  // ======================================================================
  class AudioSync {
    /**
     * @param {{onColor:(rgb:number[])=>void, onFrame?:(bins:Uint8Array, level:number, rgb:number[])=>void,
     *          onStop?:()=>void, getBase?:()=>number[]}} handlers
     */
    constructor(handlers) {
      this.h = handlers;
      this.running = false;
      this.source = null;
    }

    async start(source, settings) {
      this.stop(true);
      this.settings = settings;
      const audioConstraints = { echoCancellation: false, noiseSuppression: false, autoGainControl: false };
      let stream;
      if (source === 'mic') {
        stream = await navigator.mediaDevices.getUserMedia({ audio: audioConstraints });
      } else {
        stream = await navigator.mediaDevices.getDisplayMedia({
          video: { frameRate: 1, width: { ideal: 320 } },
          audio: audioConstraints,
          systemAudio: 'include',
          selfBrowserSurface: 'exclude',
        });
        if (!stream.getAudioTracks().length) {
          stream.getTracks().forEach((t) => t.stop());
          throw new Error('No se compartió audio. Elige «Pantalla completa» y activa «Compartir también el audio del sistema».');
        }
      }
      this.source = source;
      this.stream = stream;
      stream.getTracks().forEach((t) => t.addEventListener('ended', () => this.running && this.stop()));

      this.ctx = new AudioContext();
      const src = this.ctx.createMediaStreamSource(new MediaStream(stream.getAudioTracks()));
      this.analyser = this.ctx.createAnalyser();
      this.analyser.fftSize = 2048;
      this.analyser.smoothingTimeConstant = 0.5;
      src.connect(this.analyser);
      this.freq = new Uint8Array(this.analyser.frequencyBinCount);

      this.peak = { bass: 0.15, mid: 0.15, high: 0.15 };
      this.avgBass = 0;
      this.hue = Math.random() * 360;
      this.flash = 0;
      this.lastBeat = 0;
      this.out = [0, 0, 0];
      this.lastSent = null;
      this.running = true;
      this.timer = setInterval(() => this._tick(), 30);
    }

    stop(silent = false) {
      const wasRunning = this.running;
      this.running = false;
      clearInterval(this.timer);
      this.stream?.getTracks().forEach((t) => t.stop());
      this.stream = null;
      if (this.ctx && this.ctx.state !== 'closed') this.ctx.close().catch(() => {});
      this.ctx = null;
      if (wasRunning && !silent) this.h.onStop?.();
    }

    _band(lo, hi) {
      const nyq = this.ctx.sampleRate / 2, n = this.freq.length;
      const a = Math.max(1, Math.floor((lo / nyq) * n));
      const b = Math.min(n - 1, Math.ceil((hi / nyq) * n));
      let sum = 0;
      for (let i = a; i <= b; i++) sum += this.freq[i];
      return sum / ((b - a + 1) * 255);
    }

    /** Centroide espectral en escala logarítmica (0 = graves, 1 = agudos). */
    _centroid() {
      const nyq = this.ctx.sampleRate / 2, n = this.freq.length;
      const lo = Math.log(40), hi = Math.log(8000);
      let num = 0, den = 0;
      for (let i = 2; i < n; i++) {
        const f = (i / n) * nyq;
        if (f < 40 || f > 8000) continue;
        const e = this.freq[i] ** 2;
        num += ((Math.log(f) - lo) / (hi - lo)) * e;
        den += e;
      }
      return den ? num / den : 0;
    }

    _tick() {
      if (!this.running || !this.analyser) return;
      this.analyser.getByteFrequencyData(this.freq);
      const st = this.settings;
      const gain = 0.35 + (st.sensitivity / 100) * 1.6;
      const now = performance.now();

      const raw = { bass: this._band(25, 160), mid: this._band(160, 2000), high: this._band(2000, 10000) };
      const n = {};
      for (const k of Object.keys(raw)) {
        this.peak[k] = Math.max(raw[k], this.peak[k] * 0.997, 0.06);
        n[k] = clamp(raw[k] / this.peak[k], 0, 1);
      }
      let level = clamp((n.bass * 0.55 + n.mid * 0.3 + n.high * 0.15) * gain, 0, 1);
      level = level ** 1.6;

      // Detección de golpes (bombo)
      this.avgBass = this.avgBass * 0.93 + raw.bass * 0.07;
      const beat = raw.bass > this.avgBass * 1.28 + 0.015 && n.bass > 0.6 && now - this.lastBeat > 150;
      if (beat) this.lastBeat = now;

      let h, s = 1, v;
      switch (st.mode) {
        case 'pulse': {
          const base = this.h.getBase?.() || [255, 0, 180];
          [h, s] = rgbToHsv(...base);
          v = level;
          if (beat) this.flash = 1;
          this.flash *= 0.85;
          v = Math.max(v, this.flash * 0.9);
          break;
        }
        case 'rainbow':
          this.hue += 0.6 + level * 4 + (beat ? 35 : 0);
          h = this.hue; v = level;
          break;
        case 'beat':
          if (beat) { this.hue += 70 + Math.random() * 150; this.flash = 1; }
          this.flash *= 0.86;
          h = this.hue; v = Math.max(this.flash, level * 0.2);
          break;
        case 'spectrum':
        default: {
          const target = this._centroid() * 280;
          const d = ((target - this.hue + 540) % 360) - 180;
          this.hue += d * 0.25;
          h = this.hue; v = level;
          if (beat) v = Math.max(v, 0.95);
        }
      }

      const floor = clamp(st.floor / 100, 0, 0.6);
      v = floor + (1 - floor) * clamp(v, 0, 1);
      const target = hsvToRgb(h, s, v);
      this.out = this.out.map((o, i) => o + (target[i] - o) * (target[i] > o ? 0.75 : 0.32));
      const rgb = this.out.map((x) => clamp(Math.round(x), 0, 255));
      if (differs(this.lastSent, rgb)) {
        this.lastSent = rgb;
        this.h.onColor(rgb);
      }
      this.h.onFrame?.(this.freq, level, rgb);
    }
  }

  // ======================================================================
  // Pantalla (ambilight)
  // ======================================================================
  class ScreenSync {
    /** @param {{onColor:(rgb:number[])=>void, onStop?:()=>void, video:HTMLVideoElement}} handlers */
    constructor(handlers) {
      this.h = handlers;
      this.video = handlers.video;
      this.running = false;
      this.canvas = document.createElement('canvas');
      this.canvas.width = 64;
      this.canvas.height = 36;
      this.cx = this.canvas.getContext('2d', { willReadFrequently: true });
    }

    async start(settings) {
      this.stop(true);
      this.settings = settings;
      const stream = await navigator.mediaDevices.getDisplayMedia({
        video: { frameRate: { ideal: 30, max: 30 }, width: { ideal: 960 } },
        audio: false,
        selfBrowserSurface: 'exclude',
        surfaceSwitching: 'include',
      });
      this.stream = stream;
      stream.getVideoTracks()[0].addEventListener('ended', () => this.running && this.stop());
      this.video.srcObject = stream;
      await this.video.play().catch(() => {});
      this.out = null;
      this.lastSent = null;
      this.running = true;
      this.timer = setInterval(() => this._tick(), 40);
    }

    stop(silent = false) {
      const wasRunning = this.running;
      this.running = false;
      clearInterval(this.timer);
      this.stream?.getTracks().forEach((t) => t.stop());
      this.stream = null;
      this.video.srcObject = null;
      if (wasRunning && !silent) this.h.onStop?.();
    }

    _region(w, h) {
      switch (this.settings.region) {
        case 'bottom': return [0, h * 0.72, w, h * 0.28];
        case 'center': return [w * 0.2, h * 0.2, w * 0.6, h * 0.6];
        case 'top': return [0, 0, w, h * 0.28];
        default: return [0, 0, w, h];
      }
    }

    _tick() {
      const v = this.video;
      if (!this.running || !v.videoWidth) return;
      const [sx, sy, sw, sh] = this._region(v.videoWidth, v.videoHeight);
      const W = this.canvas.width, H = this.canvas.height;
      this.cx.drawImage(v, sx, sy, sw, sh, 0, 0, W, H);
      const d = this.cx.getImageData(0, 0, W, H).data;
      const vivid = this.settings.algo === 'vivid';
      let r = 0, g = 0, b = 0, w = 0;
      for (let i = 0; i < d.length; i += 4) {
        const R = d[i], G = d[i + 1], B = d[i + 2];
        let wt = 1;
        if (vivid) {
          const mx = Math.max(R, G, B), mn = Math.min(R, G, B);
          const sat = mx ? (mx - mn) / mx : 0;
          wt = 0.03 + sat * sat * (mx / 255) * 6;
        }
        r += R * wt; g += G * wt; b += B * wt; w += wt;
      }
      let [hh, ss, vv] = rgbToHsv(r / w, g / w, b / w);
      ss = clamp(ss * (this.settings.saturation / 100), 0, 1);
      vv = clamp(vv * 1.1, 0, 1);
      const c = hsvToRgb(hh, ss, vv);
      const alpha = 1 - (this.settings.smoothing / 100) * 0.93;
      this.out = this.out ? this.out.map((o, i) => o + (c[i] - o) * alpha) : c;
      const rgb = this.out.map((x) => clamp(Math.round(x), 0, 255));
      if (differs(this.lastSent, rgb)) {
        this.lastSent = rgb;
        this.h.onColor(rgb);
      }
    }
  }

  global.AudioSync = AudioSync;
  global.ScreenSync = ScreenSync;
})(window);
