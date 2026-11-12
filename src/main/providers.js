'use strict';
/**
 * Провайдеры LLM: OpenRouter, OpenAI (оба через SDK `openai`) и Anthropic (SDK `@anthropic-ai/sdk`).
 *
 * Внутренний формат истории (provider-neutral):
 *   { role:'user', text, images?:[{mime,data}], ctx? }
 *   { role:'assistant', text, toolCalls?:[{id,name,args}] }
 *   { role:'tool', toolCallId, name, text, images?, isError? }
 *
 * Каждый провайдер умеет:
 *   convertHistory(internal[]) -> native[]
 *   step({...}) -> { native, text, toolCalls, stop }   — один вызов модели (стриминг)
 *   toolResultsNative(results) -> native[]
 */
const Anthropic = require('@anthropic-ai/sdk').default;
const OpenAI = require('openai').default;

const OPENROUTER_BASE = 'https://openrouter.ai/api/v1';

class ProviderError extends Error {
  constructor(message, { status, retryable } = {}) {
    super(message);
    this.status = status;
    this.retryable = !!retryable;
  }
}

function friendlyError(provider, err) {
  if (err instanceof ProviderError) return err;
  const name = { openrouter: 'OpenRouter', anthropic: 'Anthropic', openai: 'OpenAI' }[provider] || provider;
  const status = err?.status;
  if (err?.name === 'AbortError' || err?.constructor?.name === 'APIUserAbortError') {
    return new ProviderError('Прервано', { status: 0 });
  }
  let msg;
  if (status === 401 || status === 403) msg = `${name}: неверный или неактивный API-ключ (${status}).`;
  else if (status === 402) msg = `${name}: недостаточно средств на балансе (402).`;
  else if (status === 404) msg = `${name}: модель не найдена — проверь название модели в настройках (404).`;
  else if (status === 429) msg = `${name}: слишком много запросов / лимит исчерпан (429). Подожди немного.`;
  else if (status >= 500) msg = `${name}: сервер временно недоступен (${status}).`;
  else if (status === 400) msg = `${name}: запрос отклонён (400): ${shortMsg(err)}`;
  else if (err?.code === 'ENOTFOUND' || err?.code === 'ECONNREFUSED' || /fetch failed|network|ECONN/i.test(err?.message || '')) {
    msg = `${name}: нет соединения с сервером. Проверь интернет.`;
  } else msg = `${name}: ${shortMsg(err)}`;
  return new ProviderError(msg, { status, retryable: status === 429 || status >= 500 });
}

function shortMsg(err) {
  const m = err?.error?.error?.message || err?.error?.message || err?.message || String(err);
  return String(m).slice(0, 300);
}

function parseArgs(s) {
  if (s && typeof s === 'object') return s;
  if (!s) return {};
  try {
    const v = JSON.parse(s);
    return v && typeof v === 'object' ? v : {};
  } catch (_) {
    return { __invalid_json: String(s).slice(0, 500) };
  }
}

// ======================================================================= OpenAI-compatible
class OpenAICompatProvider {
  constructor(kind, getKey) {
    this.kind = kind; // 'openai' | 'openrouter'
    this.getKey = getKey;
  }

  client() {
    const apiKey = this.getKey(this.kind);
    if (!apiKey) throw new ProviderError(`Не задан API-ключ ${this.kind === 'openai' ? 'OpenAI' : 'OpenRouter'}. Открой настройки.`);
    if (this.kind === 'openrouter') {
      return new OpenAI({
        apiKey,
        baseURL: OPENROUTER_BASE,
        maxRetries: 2,
        defaultHeaders: { 'HTTP-Referer': 'https://github.com/vera-assistant', 'X-Title': 'Vera Assistant' },
      });
    }
    // VERA_OPENAI_BASE_URL — для тестов или локальных OpenAI-совместимых серверов
    return new OpenAI({ apiKey, maxRetries: 2, baseURL: process.env.VERA_OPENAI_BASE_URL || undefined });
  }

  convertHistory(history) {
    const out = [];
    let pendingImages = [];
    const flushImages = () => {
      if (!pendingImages.length) return;
      out.push({
        role: 'user',
        content: [
          { type: 'text', text: '(Изображения, полученные инструментами выше.)' },
          ...pendingImages.map((im) => ({ type: 'image_url', image_url: { url: `data:${im.mime};base64,${im.data}` } })),
        ],
      });
      pendingImages = [];
    };
    for (const m of history) {
      if (m.role !== 'tool') flushImages();
      if (m.role === 'user') {
        const text = (m.ctx ? `<context>${m.ctx}</context>\n` : '') + (m.text || '');
        if (m.images?.length) {
          out.push({
            role: 'user',
            content: [
              { type: 'text', text },
              ...m.images.map((im) => ({ type: 'image_url', image_url: { url: `data:${im.mime};base64,${im.data}` } })),
            ],
          });
        } else {
          out.push({ role: 'user', content: text });
        }
      } else if (m.role === 'assistant') {
        const msg = { role: 'assistant', content: m.text || null };
        if (m.toolCalls?.length) {
          msg.tool_calls = m.toolCalls.map((tc) => ({
            id: tc.id,
            type: 'function',
            function: { name: tc.name, arguments: JSON.stringify(tc.args || {}) },
          }));
        }
        if (!msg.content && !msg.tool_calls) msg.content = '…';
        out.push(msg);
      } else if (m.role === 'tool') {
        out.push({ role: 'tool', tool_call_id: m.toolCallId, content: m.text || '(пусто)' });
        if (m.images?.length) pendingImages.push(...m.images);
      }
    }
    flushImages();
    return out;
  }

  toolResultsNative(results) {
    return this.convertHistory(results.map((r) => ({
      role: 'tool', toolCallId: r.id, name: r.name, text: r.text, images: r.images,
    })));
  }

  isReasoningModel(model) {
    const m = model.replace(/^openai\//, '');
    return /^(o\d|gpt-5)/.test(m);
  }

  async step({ model, system, messages, tools, effort, webSearch, onText, signal }) {
    const client = this.client();
    const params = {
      model,
      messages: [{ role: 'system', content: system }, ...messages],
      stream: true,
    };
    if (tools?.length) {
      params.tools = tools.map((t) => ({
        type: 'function',
        function: { name: t.name, description: t.description, parameters: t.parameters },
      }));
    }
    if (this.kind === 'openai') {
      params.max_completion_tokens = 16000;
      if (this.isReasoningModel(model)) params.reasoning_effort = effort || 'low';
    } else {
      if (webSearch) params.plugins = [{ id: 'web', max_results: 5 }];
      if (this.isReasoningModel(model) || /^(anthropic|google|x-ai|deepseek)\//.test(model)) {
        params.reasoning = { effort: effort || 'low', exclude: true };
      }
    }

    let text = '';
    const acc = [];
    let finish = null;
    try {
      const stream = await client.chat.completions.create(params, { signal });
      for await (const chunk of stream) {
        const choice = chunk.choices?.[0];
        if (!choice) continue;
        const d = choice.delta || {};
        if (d.content) {
          text += d.content;
          onText?.(d.content);
        }
        if (d.tool_calls) {
          for (const tc of d.tool_calls) {
            const i = tc.index ?? 0;
            acc[i] ||= { id: '', name: '', args: '' };
            if (tc.id) acc[i].id = tc.id;
            if (tc.function?.name && !acc[i].name) acc[i].name = tc.function.name;
            if (tc.function?.arguments) acc[i].args += tc.function.arguments;
          }
        }
        if (choice.finish_reason) finish = choice.finish_reason;
      }
    } catch (err) {
      throw friendlyError(this.kind, err);
    }

    const toolCalls = acc.filter(Boolean).filter((t) => t.name).map((t, i) => ({
      id: t.id || `call_${Date.now().toString(36)}_${i}`,
      name: t.name,
      args: parseArgs(t.args),
    }));
    const native = { role: 'assistant', content: text || null };
    if (toolCalls.length) {
      native.tool_calls = toolCalls.map((tc) => ({
        id: tc.id, type: 'function', function: { name: tc.name, arguments: JSON.stringify(tc.args) },
      }));
    }
    if (!native.content && !native.tool_calls) native.content = '…';
    return { native, text, toolCalls, stop: finish === 'length' ? 'max_tokens' : (toolCalls.length ? 'tool_use' : 'end') };
  }

  async listModels() {
    if (this.kind === 'openrouter') {
      const res = await fetch(`${OPENROUTER_BASE}/models`);
      if (!res.ok) throw new Error(`OpenRouter: ${res.status}`);
      const json = await res.json();
      return (json.data || [])
        .map((m) => ({
          id: m.id,
          name: m.name || m.id,
          tools: (m.supported_parameters || []).includes('tools'),
          vision: (m.architecture?.input_modalities || []).includes('image'),
        }))
        .sort((a, b) => a.id.localeCompare(b.id));
    }
    try {
      const client = this.client();
      const list = [];
      for await (const m of client.models.list()) list.push(m.id);
      return list
        .filter((id) => /^(gpt|o\d|chatgpt)/.test(id) && !/(audio|realtime|transcribe|tts|image|search|embedding|instruct)/.test(id))
        .sort()
        .map((id) => ({ id, name: id, tools: true, vision: true }));
    } catch (err) {
      throw friendlyError(this.kind, err);
    }
  }

  async test() {
    const client = this.client();
    try {
      if (this.kind === 'openrouter') {
        const res = await fetch(`${OPENROUTER_BASE}/key`, { headers: { Authorization: `Bearer ${this.getKey('openrouter')}` } });
        if (!res.ok) throw Object.assign(new Error('auth'), { status: res.status });
        const j = await res.json();
        const left = j?.data?.limit_remaining;
        return 'Ключ работает' + (left != null ? `, остаток лимита: ${left}` : '');
      }
      const page = await client.models.list();
      return `Ключ работает (${page.data?.length ?? 0}+ моделей)`;
    } catch (err) {
      throw friendlyError(this.kind, err);
    }
  }
}

// ======================================================================= Anthropic
const MODERN_CLAUDE = /claude-(fable|mythos)-5|claude-(opus|sonnet|haiku)-5|claude-opus-4-[6-9]|claude-sonnet-4-6/;
const FALLBACK_MODELS = /^claude-(fable-5-2|fable-5-1|opus-5-5|opus-5|sonnet-5-5)$/;
const WEBSEARCH_NEW = /claude-(fable|mythos)-5|claude-(opus|sonnet)-5|claude-opus-4-[6-9]|claude-sonnet-4-6/;

class AnthropicProvider {
  constructor(getKey) {
    this.kind = 'anthropic';
    this.getKey = getKey;
  }

  client() {
    const apiKey = this.getKey('anthropic');
    if (!apiKey) throw new ProviderError('Не задан API-ключ Anthropic. Открой настройки.');
    return new Anthropic({ apiKey, maxRetries: 2 });
  }

  /**
   * История в формате Messages API. Thinking-блоки прошлых ходов не сохраняем —
   * удаление «ведущей» серии thinking-блоков допустимо, а внутри текущего хода
   * агент дописывает ответы модели как есть (append-only).
   */
  convertHistory(history) {
    const out = [];
    let toolBuf = [];
    const flushTools = () => {
      if (!toolBuf.length) return;
      out.push({ role: 'user', content: toolBuf });
      toolBuf = [];
    };
    for (const m of history) {
      if (m.role !== 'tool') flushTools();
      if (m.role === 'user') {
        const content = [];
        for (const im of m.images || []) {
          content.push({ type: 'image', source: { type: 'base64', media_type: im.mime, data: im.data } });
        }
        if (m.ctx) content.push({ type: 'text', text: `<context>${m.ctx}</context>` });
        content.push({ type: 'text', text: m.text || '…' });
        out.push({ role: 'user', content });
      } else if (m.role === 'assistant') {
        const content = [];
        if (m.text) content.push({ type: 'text', text: m.text });
        for (const tc of m.toolCalls || []) {
          content.push({ type: 'tool_use', id: tc.id, name: tc.name, input: tc.args || {} });
        }
        if (!content.length) content.push({ type: 'text', text: '…' });
        out.push({ role: 'assistant', content });
      } else if (m.role === 'tool') {
        const content = [{ type: 'text', text: m.text || '(пусто)' }];
        for (const im of m.images || []) {
          content.push({ type: 'image', source: { type: 'base64', media_type: im.mime, data: im.data } });
        }
        const block = { type: 'tool_result', tool_use_id: m.toolCallId, content };
        if (m.isError) block.is_error = true;
        toolBuf.push(block);
      }
    }
    flushTools();
    return out;
  }

  toolResultsNative(results) {
    return this.convertHistory(results.map((r) => ({
      role: 'tool', toolCallId: r.id, name: r.name, text: r.text, images: r.images, isError: r.isError,
    })));
  }

  async step({ model, system, messages, tools, effort, webSearch, onText, signal }) {
    const client = this.client();
    const modern = MODERN_CLAUDE.test(model);
    const params = {
      model,
      max_tokens: modern || /claude-(haiku|sonnet|opus)-4/.test(model) ? 32000 : 8192,
      system,
      messages,
      tools: (tools || []).map((t) => ({
        name: t.name,
        description: t.description,
        input_schema: t.parameters,
        eager_input_streaming: true,
      })),
    };
    const betas = [];
    if (modern) {
      params.thinking = { type: 'adaptive' };
      params.output_config = { effort: effort || 'low' };
    }
    if (FALLBACK_MODELS.test(model)) {
      // при отказе классификатора API само повторит запрос на запасной модели
      betas.push('server-side-fallback-2026-07-01');
      params.fallbacks = 'default';
    }
    if (webSearch) {
      params.tools.push({ type: WEBSEARCH_NEW.test(model) ? 'web_search_20260209' : 'web_search_20250305', name: 'web_search', max_uses: 5 });
    }
    if (!params.tools.length) delete params.tools;
    if (betas.length) params.betas = betas;

    let message;
    let jsonRetries = 0;
    let emitted = '';
    for (;;) {
      try {
        const stream = client.beta.messages.stream(params, { signal });
        stream.on('text', (delta) => {
          emitted += delta;
          onText?.(delta);
        });
        message = await stream.finalMessage();
        break;
      } catch (err) {
        if (err instanceof Anthropic.APIError || err?.name === 'AbortError' || signal?.aborted) {
          throw friendlyError('anthropic', err);
        }
        // tool input не распарсился (eager input streaming) — переспрашиваем ход
        if (jsonRetries++ >= 2) throw friendlyError('anthropic', err);
        if (emitted) onText?.('\n');
      }
    }

    const text = message.content.filter((b) => b.type === 'text').map((b) => b.text).join('');
    const toolUse = message.content.filter((b) => b.type === 'tool_use');
    const native = { role: 'assistant', content: message.content };
    const sr = message.stop_reason;

    if (sr === 'refusal') {
      return { native, text: text || 'Не могу с этим помочь.', toolCalls: [], stop: 'refusal' };
    }
    if (sr === 'pause_turn') {
      return { native, text, toolCalls: [], stop: 'pause' };
    }
    if (sr === 'max_tokens' && toolUse.length) {
      throw new ProviderError('Ответ модели обрезан (max_tokens) посреди вызова инструмента. Попробуй ещё раз.');
    }
    const toolCalls = toolUse.map((b) => ({ id: b.id, name: b.name, args: b.input && typeof b.input === 'object' ? b.input : {} }));
    return { native, text, toolCalls, stop: toolCalls.length ? 'tool_use' : (sr === 'max_tokens' ? 'max_tokens' : 'end') };
  }

  async listModels() {
    try {
      const client = this.client();
      const list = [];
      for await (const m of client.models.list()) list.push({ id: m.id, name: m.display_name || m.id, tools: true, vision: true });
      return list;
    } catch (err) {
      throw friendlyError('anthropic', err);
    }
  }

  async test() {
    const models = await this.listModels();
    return `Ключ работает (${models.length} моделей)`;
  }
}

function createProviders(getKey) {
  return {
    openrouter: new OpenAICompatProvider('openrouter', getKey),
    openai: new OpenAICompatProvider('openai', getKey),
    anthropic: new AnthropicProvider(getKey),
  };
}

module.exports = { createProviders, ProviderError, friendlyError };
