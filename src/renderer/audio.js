/* Аудио Веры: микрофон с VAD → WAV, очередь синтеза речи с липсинком, звуки. */
(function () {
  'use strict';

  // ---------------------------------------------------------------- WAV
  function downsample(f32, from, to) {
    if (from === to) return f32;
    const ratio = from / to;
    const len = Math.floor(f32.length / ratio);
    const out = new Float32Array(len);
    let pos = 0;
    for (let i = 0; i < len; i++) {
      const next = Math.floor((i + 1) * ratio);
      let sum = 0; let n = 0;
      for (let j = Math.floor(pos); j < next && j < f32.length; j++) { sum += f32[j]; n++; }
      out[i] = n ? sum / n : 0;
      pos = next;
    }
    return out;
  }

  function encodeWav(f32, rate) {
    const buf = new ArrayBuffer(44 + f32.length * 2);
    const v = new DataView(buf);
    const w = (o, s) => { for (let i = 0; i < s.length; i++) v.setUint8(o + i, s.charCodeAt(i)); };
    w(0, 'RIFF'); v.setUint32(4, 36 + f32.length * 2, true); w(8, 'WAVE');
    w(12, 'fmt '); v.setUint32(16, 16, true); v.setUint16(20, 1, true); v.setUint16(22, 1, true);
    v.setUint32(24, rate, true); v.setUint32(28, rate * 2, true); v.setUint16(32, 2, true); v.setUint16(34, 16, true);
    w(36, 'data'); v.setUint32(40, f32.length * 2, true);
    let o = 44;
    for (let i = 0; i < f32.length; i++, o += 2) {
      const s = Math.max(-1, Math.min(1, f32[i]));
      v.setInt16(o, s < 0 ? s * 0x8000 : s * 0x7fff, true);
    }
    return new Uint8Array(buf);
  }

  function concat(chunks) {
    let n = 0;
    for (const c of chunks) n += c.length;
    const out = new Float32Array(n);
    let o = 0;
    for (const c of chunks) { out.set(c, o); o += c.length; }
    return out;
  }

  function rms(f32) {
    let s = 0;
    for (let i = 0; i < f32.length; i++) s += f32[i] * f32[i];
    return Math.sqrt(s / (f32.length || 1));
  }

  const WORKLET = `
    class Tap extends AudioWorkletProcessor {
      constructor() { super(); this.buf = new Float32Array(2048); this.n = 0; }
      process(inputs) {
        const ch = inputs[0] && inputs[0][0];
        if (ch) {
          for (let i = 0; i < ch.length; i++) {
            this.buf[this.n++] = ch[i];
            if (this.n === this.buf.length) { this.port.postMessage(this.buf.slice(0)); this.n = 0; }
          }
        }
        return true;
      }
    }
    registerProcessor('vera-tap', Tap);
  `;

  // ---------------------------------------------------------------- Mic + VAD
  /**
   * Режимы: 'wake' — постоянно слушает фразы и отдаёт сегменты; 'command' — одна фраза.
   * onSegment(wavBytes, {mode, durationMs}) ; onLevel(0..1) ; onState(state)
   */
  class Listener {
    constructor(opts) {
      this.opts = opts;
      this.ctx = null;
      this.stream = null;
      this.mode = 'off';
      this.rec = null;
      this.pre = [];
      this.loudMs = 0;
      this.silentMs = 0;
      this.noise = 0.008;
      this.commandStarted = 0;
      this.paused = false;
    }

    get running() { return !!this.ctx; }

    threshold() {
      const sens = Math.max(0, Math.min(1, this.opts.sensitivity()));
      const base = 0.055 - sens * 0.045; // 0.055 .. 0.01
      return Math.max(base, this.noise * 2.6);
    }

    async ensure() {
      if (this.ctx) return;
      const deviceId = this.opts.deviceId?.();
      this.stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          deviceId: deviceId ? { exact: deviceId } : undefined,
          channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true,
        },
      });
      this.ctx = new AudioContext();
      const url = URL.createObjectURL(new Blob([WORKLET], { type: 'application/javascript' }));
      await this.ctx.audioWorklet.addModule(url);
      URL.revokeObjectURL(url);
      const src = this.ctx.createMediaStreamSource(this.stream);
      const node = new AudioWorkletNode(this.ctx, 'vera-tap');
      const mute = this.ctx.createGain();
      mute.gain.value = 0;
      src.connect(node); node.connect(mute); mute.connect(this.ctx.destination);
      node.port.onmessage = (e) => this.onChunk(e.data);
      this.rate = this.ctx.sampleRate;
    }

    async startWake() {
      await this.ensure();
      if (this.mode !== 'command') this.mode = 'wake';
      this.opts.onState?.(this.mode);
    }

    async startCommand() {
      await this.ensure();
      this.mode = 'command';
      this.rec = null;
      this.loudMs = 0;
      this.commandStarted = performance.now();
      this.opts.onState?.('command');
    }

    /** Завершить фразу досрочно (повторное нажатие горячей клавиши). */
    finishNow() {
      if (this.mode !== 'command') return;
      if (this.rec && this.rec.length) this.finish();
      else this.cancelCommand('cancel');
    }

    cancelCommand(reason) {
      this.rec = null;
      this.mode = this.opts.wakeEnabled() ? 'wake' : 'off';
      this.opts.onState?.(this.mode, reason);
      if (this.mode === 'off') this.shutdown(true);
    }

    shutdown(silent) {
      this.mode = 'off';
      this.rec = null;
      try { this.stream?.getTracks().forEach((t) => t.stop()); } catch (_) { /* */ }
      try { this.ctx?.close(); } catch (_) { /* */ }
      this.stream = null;
      this.ctx = null;
      if (!silent) this.opts.onState?.('off');
    }

    onChunk(chunk) {
      if (this.mode === 'off') return;
      const dur = (chunk.length / this.rate) * 1000;
      const level = rms(chunk);
      this.opts.onLevel?.(Math.min(1, level / 0.15));
      if (this.paused && this.mode === 'wake') { this.pre = []; this.rec = null; return; }
      const thr = this.threshold();

      if (!this.rec) {
        this.pre.push(chunk);
        while (this.pre.length * dur > 450) this.pre.shift();
        if (level > thr) {
          this.loudMs += dur;
          if (this.loudMs >= 110) {
            this.rec = this.pre.slice();
            this.pre = [];
            this.silentMs = 0;
            this.recStart = performance.now();
            if (this.mode === 'command') this.opts.onState?.('recording');
          }
        } else {
          this.loudMs = 0;
          this.noise = this.noise * 0.97 + level * 0.03;
        }
        if (this.mode === 'command' && performance.now() - this.commandStarted > 7000) {
          this.cancelCommand('timeout');
        }
        return;
      }

      this.rec.push(chunk);
      if (level > thr * 0.7) this.silentMs = 0;
      else this.silentMs += dur;
      const recMs = performance.now() - this.recStart;
      const maxMs = this.mode === 'command' ? 30000 : 9000;
      if (this.silentMs >= this.opts.silenceMs() || recMs >= maxMs) this.finish();
    }

    finish() {
      const chunks = this.rec || [];
      const mode = this.mode;
      this.rec = null;
      this.loudMs = 0;
      const f32 = concat(chunks);
      const durationMs = (f32.length / this.rate) * 1000;
      if (mode === 'command') {
        this.mode = this.opts.wakeEnabled() ? 'wake' : 'off';
        if (this.mode === 'off') this.shutdown(true);
        this.opts.onState?.('processing');
      }
      if (durationMs < 350) {
        if (mode === 'command') this.opts.onState?.(this.mode, 'short');
        return;
      }
      const wav = encodeWav(downsample(f32, this.rate || 48000, 16000), 16000);
      this.opts.onSegment?.(wav, { mode, durationMs });
    }
  }

  // ---------------------------------------------------------------- Speaker
  /** Очередь фраз: синтез заранее, проигрывание по очереди, уровень для липсинка. */
  class Speaker {
    constructor(opts) {
      this.opts = opts; // { volume(), rate(), systemVoice(), onLevel(v), onStart(), onEnd(), onError(e) }
      this.queue = [];
      this.playing = false;
      this.gen = 0;
      this.ctx = null;
      this.analyser = null;
      this.current = null;
      this.raf = null;
    }

    get active() { return this.playing || this.queue.length > 0; }

    audioCtx() {
      if (!this.ctx) {
        this.ctx = new AudioContext();
        this.analyser = this.ctx.createAnalyser();
        this.analyser.fftSize = 512;
        this.analyser.connect(this.ctx.destination);
      }
      return this.ctx;
    }

    enqueue(text) {
      const t = String(text || '').trim();
      if (!t) return;
      const gen = this.gen;
      const item = { text: t, gen, audio: window.vera.voice.tts(t).catch((e) => ({ error: e })) };
      this.queue.push(item);
      this.pump();
    }

    stop() {
      this.gen++;
      this.queue = [];
      try { this.current?.pause(); } catch (_) { /* */ }
      try { speechSynthesis.cancel(); } catch (_) { /* */ }
      this.current = null;
      if (this.playing) {
        this.playing = false;
        this.opts.onEnd?.();
      }
    }

    async pump() {
      if (this.playing) return;
      const item = this.queue.shift();
      if (!item) return;
      this.playing = true;
      this.opts.onStart?.();
      try {
        const r = await item.audio;
        if (item.gen !== this.gen) return;
        if (r.error) throw r.error;
        if (r.engine === 'system') await this.playSystem(r.text, item.gen);
        else if (r.audio) await this.playAudio(r, item.gen);
      } catch (e) {
        this.opts.onError?.(e);
      } finally {
        if (item.gen === this.gen) {
          this.playing = false;
          if (!this.queue.length) this.opts.onEnd?.();
          this.pump();
        }
      }
    }

    playAudio(r, gen) {
      return new Promise((resolve) => {
        const ctx = this.audioCtx();
        if (ctx.state === 'suspended') ctx.resume();
        const blob = new Blob([r.audio], { type: r.mime || 'audio/mpeg' });
        const url = URL.createObjectURL(blob);
        const el = new Audio(url);
        el.preservesPitch = true;
        el.playbackRate = Math.max(0.5, Math.min(2, this.opts.rate()));
        el.volume = Math.max(0, Math.min(1, this.opts.volume()));
        const src = ctx.createMediaElementSource(el);
        src.connect(this.analyser);
        this.current = el;
        const data = new Float32Array(this.analyser.fftSize);
        const tick = () => {
          if (this.current !== el) return;
          this.analyser.getFloatTimeDomainData(data);
          this.opts.onLevel?.(Math.min(1, rms(data) / 0.12));
          this.raf = requestAnimationFrame(tick);
        };
        const done = () => {
          cancelAnimationFrame(this.raf);
          this.opts.onLevel?.(0);
          URL.revokeObjectURL(url);
          try { src.disconnect(); } catch (_) { /* */ }
          if (this.current === el) this.current = null;
          resolve();
        };
        el.onended = done;
        el.onerror = done;
        el.onpause = () => { if (gen !== this.gen) done(); };
        el.play().then(() => tick()).catch(done);
      });
    }

    playSystem(text, gen) {
      return new Promise((resolve) => {
        const u = new SpeechSynthesisUtterance(text);
        const voices = speechSynthesis.getVoices();
        const want = this.opts.systemVoice();
        u.voice = voices.find((v) => v.name === want) || voices.find((v) => /^ru/i.test(v.lang)) || null;
        u.lang = u.voice?.lang || 'ru-RU';
        u.rate = Math.max(0.5, Math.min(2, this.opts.rate()));
        u.volume = Math.max(0, Math.min(1, this.opts.volume()));
        u.pitch = 1.15;
        let flap = setInterval(() => { this.opts.onLevel?.(Math.random() > 0.45 ? 0.7 : 0.05); }, 110);
        const done = () => {
          clearInterval(flap);
          flap = null;
          this.opts.onLevel?.(0);
          resolve();
        };
        u.onend = done;
        u.onerror = done;
        if (gen !== this.gen) return done();
        speechSynthesis.speak(u);
      });
    }
  }

  // ---------------------------------------------------------------- sounds
  let sfxCtx = null;
  function tone(seq, volume = 0.15) {
    sfxCtx = sfxCtx || new AudioContext();
    const ctx = sfxCtx;
    if (ctx.state === 'suspended') ctx.resume();
    let t = ctx.currentTime + 0.02;
    for (const [freq, dur, type = 'triangle'] of seq) {
      const o = ctx.createOscillator();
      const g = ctx.createGain();
      o.type = type;
      o.frequency.setValueAtTime(freq, t);
      g.gain.setValueAtTime(0.0001, t);
      g.gain.exponentialRampToValueAtTime(volume, t + 0.015);
      g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
      o.connect(g); g.connect(ctx.destination);
      o.start(t); o.stop(t + dur + 0.02);
      t += dur * 0.8;
    }
  }

  const Sfx = {
    listenStart: () => tone([[660, 0.09], [990, 0.12]], 0.12),
    listenStop: () => tone([[880, 0.08], [587, 0.12]], 0.1),
    reminder: () => tone([[784, 0.16, 'sine'], [988, 0.16, 'sine'], [1175, 0.28, 'sine'], [988, 0.14, 'sine'], [1319, 0.4, 'sine']], 0.18),
    pop: () => tone([[520, 0.06, 'square'], [780, 0.07, 'square']], 0.05),
    error: () => tone([[330, 0.12], [262, 0.2]], 0.1),
  };

  window.VeraAudio = { Listener, Speaker, Sfx, encodeWav };
})();
