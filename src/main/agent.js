'use strict';
/**
 * Агентный цикл Веры: история диалога, вызов модели, выполнение инструментов, эмоции.
 *
 * События (emit 'event', payload):
 *   { type:'turn-start', turnId, source, user }
 *   { type:'delta', turnId, step, delta }
 *   { type:'emotion', turnId, emotion }
 *   { type:'tool', turnId, id, name, status:'run'|'done', label, isError }
 *   { type:'turn-end', turnId, text, emotion, source }
 *   { type:'turn-error', turnId, message }
 */
const { EventEmitter } = require('events');
const { uid, timeContext } = require('./util');
const { buildSystemPrompt, EMOTIONS } = require('./personas');

const MAX_STEPS = 10;
const MAX_SAVED = 300;
const TAG_RE = new RegExp(`\\[(${EMOTIONS.join('|')})\\]\\s*`, 'gi');

/** Вырезает тег эмоции в начале текста при стриминге. */
class TagFilter {
  constructor(onEmotion) {
    this.onEmotion = onEmotion;
    this.head = true;
    this.buf = '';
  }

  push(delta) {
    if (!this.head) return delta.replace(TAG_RE, (m, e) => { this.onEmotion(e.toLowerCase()); return ''; });
    this.buf += delta;
    const trimmed = this.buf.replace(/^\s+/, '');
    if (!trimmed) return '';
    if (trimmed[0] !== '[') { this.head = false; return this.flush(); }
    const close = trimmed.indexOf(']');
    if (close === -1) {
      if (trimmed.length > 16) { this.head = false; return this.flush(); }
      return '';
    }
    this.head = false;
    const tag = trimmed.slice(1, close).toLowerCase();
    if (EMOTIONS.includes(tag)) {
      this.onEmotion(tag);
      this.buf = trimmed.slice(close + 1).replace(/^\s+/, '');
    }
    return this.flush();
  }

  flush() {
    const out = this.buf.replace(TAG_RE, (m, e) => { this.onEmotion(e.toLowerCase()); return ''; });
    this.buf = '';
    return out;
  }
}

function stripTags(s) {
  return String(s || '').replace(TAG_RE, '').trim();
}

class Agent extends EventEmitter {
  constructor({ store, providers, tools, memory, historyFile }) {
    super();
    this.store = store;
    this.providers = providers;
    this.tools = tools;
    this.memory = memory;
    this.historyFile = historyFile;
    if (!Array.isArray(this.historyFile.data)) this.historyFile.data = [];
    this.history = this.historyFile.data; // в памяти — с картинками текущей сессии
    this.busy = false;
    this.abort = null;
    this.currentTurn = null;
  }

  emitEvent(e) {
    this.emit('event', e);
  }

  /** История для показа в чате (без служебных деталей). */
  visibleHistory() {
    const out = [];
    for (const m of this.history) {
      if (m.role === 'user' && !m.hidden) out.push({ role: 'user', text: m.text, ts: m.ts, hasImage: !!(m.images?.length || m.hadImages) });
      else if (m.role === 'assistant') {
        const last = out[out.length - 1];
        const chips = (m.toolCalls || []).map((tc) => ({ id: tc.id, name: tc.name, label: tc.label || tc.name }));
        if (last && last.role === 'assistant' && last.turnId && last.turnId === m.turnId) {
          if (m.text) last.text = (last.text ? last.text + '\n\n' : '') + stripTags(m.text);
          last.tools.push(...chips);
        } else {
          out.push({ role: 'assistant', text: stripTags(m.text), ts: m.ts, turnId: m.turnId, tools: chips, emotion: m.emotion });
        }
      } else if (m.role === 'tool') {
        // обновляем подписи чипов результатами
        for (let i = out.length - 1; i >= 0; i--) {
          const chip = out[i].tools?.find((c) => c.id === m.toolCallId);
          if (chip) { chip.label = m.label || chip.label; chip.isError = !!m.isError; break; }
        }
      }
    }
    return out;
  }

  clear() {
    if (this.busy) this.stop();
    this.historyFile.data = [];
    this.history = this.historyFile.data;
    this.historyFile.save();
  }

  stop() {
    if (this.abort) this.abort.abort();
  }

  persist() {
    // картинки на диск не пишем
    let data = this.history.map((m) => {
      if (!m.images?.length) return m;
      const { images, ...rest } = m;
      return { ...rest, hadImages: true, text: (m.text || '') + (m.role === 'user' ? '' : '') };
    });
    if (data.length > MAX_SAVED) data = data.slice(data.length - MAX_SAVED);
    this.historyFile.data = data;
    // в памяти держим полную историю, но не бесконечно
    if (this.history.length > MAX_SAVED * 2) this.history.splice(0, this.history.length - MAX_SAVED);
    this.historyFile.save();
    this.historyFile.data = data;
  }

  /** Последние N сообщений, начиная с user-сообщения (не разрывая пары tool_use/tool_result). */
  contextWindow() {
    const limit = Math.max(6, Number(this.store.settings.historyLimit) || 40);
    let start = Math.max(0, this.history.length - limit);
    while (start < this.history.length && this.history[start].role !== 'user') start++;
    const slice = this.history.slice(start);
    // картинки сохраняем только у двух последних ходов — экономия токенов
    const userIdx = slice.map((m, i) => (m.role === 'user' ? i : -1)).filter((i) => i >= 0);
    const keepFrom = userIdx.length > 2 ? userIdx[userIdx.length - 2] : 0;
    return slice.map((m, i) => {
      if (i >= keepFrom || !m.images?.length) {
        if (m.hadImages && !m.images) return { ...m, text: (m.text || '') + ' [к сообщению было приложено изображение]' };
        return m;
      }
      const { images, ...rest } = m;
      return { ...rest, text: (m.text || '') + ' [к сообщению было приложено изображение]' };
    });
  }

  /**
   * @param {{text:string, images?:Array<{mime,data}>, source?:string, hidden?:boolean}} input
   */
  async send(input) {
    if (this.busy) throw new Error('Вера ещё отвечает на прошлое сообщение');
    const settings = this.store.settings;
    const providerName = settings.provider;
    const provider = this.providers[providerName];
    if (!provider) throw new Error('Неизвестный провайдер: ' + providerName);
    const model = settings.models?.[providerName];
    const text = String(input.text || '').trim();
    if (!text && !input.images?.length) throw new Error('Пустое сообщение');

    this.busy = true;
    this.abort = new AbortController();
    const signal = this.abort.signal;
    const turnId = uid('t_');
    const source = input.source || 'chat';
    this.currentTurn = { turnId, source };

    const userMsg = {
      role: 'user',
      text: text || 'Что на этом изображении?',
      images: input.images?.length ? input.images : undefined,
      ctx: timeContext(),
      ts: Date.now(),
      source,
      hidden: !!input.hidden,
    };
    this.history.push(userMsg);
    this.emitEvent({ type: 'turn-start', turnId, source, user: { text: userMsg.text, hasImage: !!userMsg.images, hidden: userMsg.hidden } });

    // system и tools фиксируем на весь ход (append-only история внутри хода)
    const toolDefs = this.tools.definitions(settings);
    const system = buildSystemPrompt(settings, this.memory.forPrompt(), toolDefs.map((t) => t.name));
    const native = provider.convertHistory(this.contextWindow());

    let emotion = null;
    let fullText = '';
    const onEmotion = (e) => {
      emotion = e;
      this.emitEvent({ type: 'emotion', turnId, emotion: e });
    };

    try {
      for (let step = 0; step < MAX_STEPS; step++) {
        const filter = new TagFilter(onEmotion);
        let stepText = '';
        let res;
        try {
          res = await provider.step({
            model,
            system,
            messages: native,
            tools: toolDefs,
            effort: settings.effort,
            webSearch: !!settings.webSearch,
            signal,
            onText: (d) => {
              const clean = filter.push(d);
              if (clean) {
                stepText += clean;
                this.emitEvent({ type: 'delta', turnId, step, delta: clean });
              }
            },
          });
        } catch (err) {
          if (signal.aborted) {
            if (stepText) {
              this.history.push({ role: 'assistant', text: stepText + ' …', ts: Date.now(), turnId, emotion });
              fullText += (fullText ? '\n\n' : '') + stepText;
            }
            throw Object.assign(new Error('Прервано'), { aborted: true });
          }
          throw err;
        }
        const tail = filter.flush();
        if (tail) {
          stepText += tail;
          this.emitEvent({ type: 'delta', turnId, step, delta: tail });
        }
        const cleanText = stripTags(res.text);
        native.push(res.native);
        const asst = { role: 'assistant', text: cleanText, ts: Date.now(), turnId, emotion };
        if (res.toolCalls.length) asst.toolCalls = res.toolCalls.map((tc) => ({ ...tc }));
        this.history.push(asst);
        if (cleanText) fullText += (fullText ? '\n\n' : '') + cleanText;

        if (res.stop === 'pause') continue; // серверный инструмент (веб-поиск) попросил продолжить
        if (!res.toolCalls.length) break;

        const results = [];
        for (const tc of res.toolCalls) {
          if (signal.aborted) {
            results.push({ id: tc.id, name: tc.name, text: 'Отменено пользователем.', isError: true, label: `⛔ ${tc.name}` });
            continue;
          }
          this.emitEvent({ type: 'tool', turnId, id: tc.id, name: tc.name, status: 'run', label: tc.name });
          const r = await this.tools.execute(tc.name, tc.args);
          results.push({ id: tc.id, name: tc.name, ...r });
          const call = asst.toolCalls.find((x) => x.id === tc.id);
          if (call) call.label = r.label;
          this.emitEvent({ type: 'tool', turnId, id: tc.id, name: tc.name, status: 'done', label: r.label || tc.name, isError: !!r.isError, kind: r.kind });
        }
        for (const r of results) {
          this.history.push({ role: 'tool', toolCallId: r.id, name: r.name, text: r.text, images: r.images, isError: !!r.isError, label: r.label, turnId });
        }
        native.push(...provider.toolResultsNative(results));
        if (signal.aborted) throw Object.assign(new Error('Прервано'), { aborted: true });
        if (step === MAX_STEPS - 1) {
          fullText += (fullText ? '\n\n' : '') + '(Слишком много шагов подряд — остановилась.)';
        }
      }
      this.emitEvent({ type: 'turn-end', turnId, text: fullText, emotion: emotion || 'neutral', source });
      return { turnId, text: fullText, emotion };
    } catch (err) {
      const message = err.aborted ? 'Прервано' : (err?.message || String(err));
      this.emitEvent({ type: 'turn-error', turnId, message, aborted: !!err.aborted, text: fullText, source });
      if (!err.aborted) throw err;
      return { turnId, text: fullText, emotion, aborted: true };
    } finally {
      this.busy = false;
      this.abort = null;
      this.currentTurn = null;
      this.persist();
    }
  }
}

module.exports = { Agent, stripTags, TagFilter };
