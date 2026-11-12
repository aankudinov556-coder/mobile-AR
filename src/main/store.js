'use strict';
/**
 * Хранилище: настройки (settings.json), секреты (secrets.json, шифруются через
 * Electron safeStorage — на Windows это DPAPI), и простые JSON-коллекции.
 */
const { app, safeStorage } = require('electron');
const fs = require('fs');
const path = require('path');

const DEFAULT_SETTINGS = {
  firstRun: true,
  provider: 'openrouter', // 'openrouter' | 'anthropic' | 'openai'
  models: {
    openrouter: 'openai/gpt-5-mini',
    anthropic: 'claude-opus-5-5',
    openai: 'gpt-5-mini',
  },
  effort: 'low', // глубина размышлений: low | medium | high
  webSearch: false,
  historyLimit: 40,

  userName: 'семпай',
  persona: 'tease', // tease | sweet | business | tsundere
  customPrompt: '',

  stt: {
    provider: 'openai', // openai | groq
    openaiModel: 'gpt-4o-mini-transcribe',
    groqModel: 'whisper-large-v3-turbo',
    language: 'ru',
  },
  tts: {
    engine: 'openai', // openai | elevenlabs | system | off
    autoSpeak: 'voice', // voice — только на голосовые запросы, always, never
    openaiModel: 'gpt-4o-mini-tts',
    openaiVoice: 'shimmer',
    openaiInstructions:
      'Говори как озорная дерзкая девушка-аниме лет двадцати: игриво, с лёгкой насмешкой и улыбкой в голосе, живо и быстро.',
    elevenVoiceId: '',
    elevenModel: 'eleven_multilingual_v2',
    systemVoice: '',
    rate: 1.0,
    volume: 0.9,
  },
  voice: {
    wakeWord: false,
    wakePhrases: 'вера, верочка, верунь',
    sensitivity: 0.5, // 0..1 — чем больше, тем чувствительнее
    silenceMs: 1100,
    micDeviceId: '',
    chime: true,
  },
  hotkeys: {
    toggleChat: 'CommandOrControl+Alt+V',
    talk: 'CommandOrControl+Alt+Space',
    screenshot: 'CommandOrControl+Alt+S',
  },
  pet: {
    visible: true,
    scale: 3,
    alwaysOnTop: true,
    x: null,
    y: null,
    idleChatter: true,
    bubbleSeconds: 9,
  },
  capabilities: {
    pcControl: true,
    screenshots: true,
    clipboard: true,
    runCommands: false,
    confirmCommands: true,
  },
  reminders: {
    speak: true,
    sound: true,
    notification: true,
  },
  appShortcuts: [], // [{ name, path }]
  autostart: false,
  theme: 'auto', // auto | dark | light
};

const SECRET_NAMES = ['openrouter', 'anthropic', 'openai', 'groq', 'elevenlabs'];

function isPlainObject(v) {
  return v && typeof v === 'object' && !Array.isArray(v);
}

function deepMerge(base, patch) {
  const out = Array.isArray(base) ? base.slice() : { ...base };
  for (const [k, v] of Object.entries(patch || {})) {
    if (isPlainObject(v) && isPlainObject(base?.[k])) out[k] = deepMerge(base[k], v);
    else out[k] = v;
  }
  return out;
}

class JsonFile {
  constructor(file, defaults) {
    this.file = file;
    this.defaults = defaults;
    this.data = null;
    this._timer = null;
  }

  load() {
    try {
      const raw = fs.readFileSync(this.file, 'utf8');
      const parsed = JSON.parse(raw);
      this.data = isPlainObject(this.defaults) ? deepMerge(this.defaults, parsed) : parsed;
    } catch (err) {
      if (err.code !== 'ENOENT') {
        // повреждённый файл — сохраняем копию и начинаем с нуля
        try { fs.copyFileSync(this.file, this.file + '.broken'); } catch (_) { /* ignore */ }
      }
      this.data = structuredClone(this.defaults);
    }
    return this.data;
  }

  saveNow() {
    clearTimeout(this._timer);
    this._timer = null;
    const tmp = this.file + '.tmp';
    fs.mkdirSync(path.dirname(this.file), { recursive: true });
    fs.writeFileSync(tmp, JSON.stringify(this.data, null, 2), 'utf8');
    fs.renameSync(tmp, this.file);
  }

  save() {
    clearTimeout(this._timer);
    this._timer = setTimeout(() => {
      try { this.saveNow(); } catch (e) { console.error('save failed', this.file, e); }
    }, 300);
  }

  flush() {
    if (this._timer) this.saveNow();
  }
}

class Store {
  constructor() {
    this.dir = app.getPath('userData');
    this.settingsFile = new JsonFile(path.join(this.dir, 'settings.json'), DEFAULT_SETTINGS);
    this.secretsFile = new JsonFile(path.join(this.dir, 'secrets.json'), {});
    this.settingsFile.load();
    this.secretsFile.load();
  }

  get settings() {
    return this.settingsFile.data;
  }

  updateSettings(patch) {
    this.settingsFile.data = deepMerge(this.settingsFile.data, patch);
    this.settingsFile.save();
    return this.settingsFile.data;
  }

  // ---------------------------------------------------------------- secrets
  getSecret(name) {
    const v = this.secretsFile.data[name];
    if (!v) return '';
    try {
      if (v.startsWith('enc:')) {
        return safeStorage.decryptString(Buffer.from(v.slice(4), 'base64'));
      }
      if (v.startsWith('b64:')) return Buffer.from(v.slice(4), 'base64').toString('utf8');
    } catch (e) {
      console.error('secret decrypt failed', name, e.message);
    }
    return '';
  }

  setSecret(name, value) {
    if (!SECRET_NAMES.includes(name)) throw new Error('Неизвестный ключ: ' + name);
    const val = String(value || '').trim();
    if (!val) {
      delete this.secretsFile.data[name];
    } else if (safeStorage.isEncryptionAvailable()) {
      this.secretsFile.data[name] = 'enc:' + safeStorage.encryptString(val).toString('base64');
    } else {
      this.secretsFile.data[name] = 'b64:' + Buffer.from(val, 'utf8').toString('base64');
    }
    this.secretsFile.saveNow();
  }

  secretStatus() {
    const out = {};
    for (const n of SECRET_NAMES) {
      const v = this.getSecret(n);
      out[n] = v ? { set: true, hint: v.length > 8 ? v.slice(0, 4) + '…' + v.slice(-4) : '••••' } : { set: false };
    }
    return out;
  }

  collection(name, defaults) {
    const f = new JsonFile(path.join(this.dir, name + '.json'), defaults);
    f.load();
    return f;
  }

  flushAll() {
    this.settingsFile.flush();
    this.secretsFile.flush();
  }
}

module.exports = { Store, DEFAULT_SETTINGS, SECRET_NAMES, deepMerge };
