'use strict';
/**
 * Голос: распознавание речи (OpenAI / Groq Whisper) и синтез (OpenAI TTS / ElevenLabs).
 * Системный голос Windows (SAPI) работает прямо в окне Веры через speechSynthesis.
 */
const OpenAI = require('openai').default;
const { toFile } = require('openai');
const { friendlyError } = require('./providers');

class Voice {
  constructor(store) {
    this.store = store;
  }

  async transcribe(buffer, mime = 'audio/wav') {
    const s = this.store.settings.stt;
    const provider = s.provider === 'groq' ? 'groq' : 'openai';
    const key = this.store.getSecret(provider);
    if (!key) {
      throw new Error(provider === 'groq'
        ? 'Для распознавания речи нужен ключ Groq (Настройки → Ключи).'
        : 'Для распознавания речи нужен ключ OpenAI (Настройки → Ключи).');
    }
    const client = provider === 'groq'
      ? new OpenAI({ apiKey: key, baseURL: 'https://api.groq.com/openai/v1', maxRetries: 1 })
      : new OpenAI({ apiKey: key, maxRetries: 1 });
    const ext = /webm/.test(mime) ? 'webm' : /ogg/.test(mime) ? 'ogg' : /mp4|m4a/.test(mime) ? 'm4a' : 'wav';
    try {
      const file = await toFile(Buffer.from(buffer), `speech.${ext}`, { type: mime });
      const res = await client.audio.transcriptions.create({
        file,
        model: provider === 'groq' ? s.groqModel : s.openaiModel,
        language: s.language || undefined,
        prompt: 'Вера, напомни мне… Вера, открой…',
      });
      return String(res.text || '').trim();
    } catch (err) {
      throw friendlyError(provider === 'groq' ? 'Groq' : 'openai', err);
    }
  }

  /** Возвращает { mime, audio: Buffer } или { engine:'system' } */
  async synthesize(text) {
    const t = this.store.settings.tts;
    const clean = cleanForSpeech(text);
    if (!clean) return { empty: true };
    if (t.engine === 'off') return { off: true };
    if (t.engine === 'system') return { engine: 'system', text: clean };

    if (t.engine === 'elevenlabs') {
      const key = this.store.getSecret('elevenlabs');
      if (!key) throw new Error('Нет ключа ElevenLabs — добавь его в настройках или выбери другой голос.');
      const voiceId = t.elevenVoiceId || 'EXAVITQu4vr4xnSDxMaL';
      const res = await fetch(`https://api.elevenlabs.io/v1/text-to-speech/${encodeURIComponent(voiceId)}?output_format=mp3_44100_128`, {
        method: 'POST',
        headers: { 'xi-api-key': key, 'Content-Type': 'application/json', Accept: 'audio/mpeg' },
        body: JSON.stringify({
          text: clean,
          model_id: t.elevenModel || 'eleven_multilingual_v2',
          voice_settings: { stability: 0.4, similarity_boost: 0.8, style: 0.35, use_speaker_boost: true },
        }),
      });
      if (!res.ok) {
        const body = await res.text().catch(() => '');
        throw new Error(`ElevenLabs: ошибка ${res.status} ${body.slice(0, 200)}`);
      }
      return { mime: 'audio/mpeg', audio: Buffer.from(await res.arrayBuffer()) };
    }

    // OpenAI
    const key = this.store.getSecret('openai');
    if (!key) throw new Error('Для голоса OpenAI нужен ключ OpenAI. Или выбери системный голос в настройках.');
    const client = new OpenAI({ apiKey: key, maxRetries: 1 });
    try {
      const params = {
        model: t.openaiModel || 'gpt-4o-mini-tts',
        voice: t.openaiVoice || 'shimmer',
        input: clean,
        response_format: 'mp3',
      };
      if (/gpt-4o/.test(params.model) && t.openaiInstructions) params.instructions = t.openaiInstructions;
      const res = await client.audio.speech.create(params);
      return { mime: 'audio/mpeg', audio: Buffer.from(await res.arrayBuffer()) };
    } catch (err) {
      throw friendlyError('openai', err);
    }
  }

  async elevenVoices() {
    const key = this.store.getSecret('elevenlabs');
    if (!key) throw new Error('Нет ключа ElevenLabs');
    const res = await fetch('https://api.elevenlabs.io/v1/voices', { headers: { 'xi-api-key': key } });
    if (!res.ok) throw new Error(`ElevenLabs: ${res.status}`);
    const j = await res.json();
    return (j.voices || []).map((v) => ({ id: v.voice_id, name: v.name, labels: v.labels || {} }));
  }
}

/** Убирает разметку, ссылки, код и эмодзи, чтобы голос читал естественно. */
function cleanForSpeech(text) {
  return String(text || '')
    .replace(/```[\s\S]*?```/g, ' (код смотри в чате) ')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]+)\]\((https?:[^)]+)\)/g, '$1')
    .replace(/https?:\/\/\S+/g, 'ссылка')
    .replace(/^\s{0,3}#{1,6}\s+/gm, '')
    .replace(/^\s*[-*•]\s+/gm, '')
    .replace(/[*_~|>#]/g, '')
    .replace(/\p{Extended_Pictographic}(️|‍\p{Extended_Pictographic})*/gu, '')
    .replace(/\s+/g, ' ')
    .trim();
}

module.exports = { Voice, cleanForSpeech };
