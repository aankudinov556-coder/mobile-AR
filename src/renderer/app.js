/* Главное окно Веры: чат, напоминания, память, настройки. */
(function () {
  'use strict';
  const api = window.vera;
  const $ = (s, root = document) => root.querySelector(s);
  const $$ = (s, root = document) => Array.from(root.querySelectorAll(s));

  let settings = null;
  let info = null;
  let secretStatus = {};
  const PROVIDER_NAMES = { openrouter: 'OpenRouter', anthropic: 'Anthropic', openai: 'OpenAI' };

  function esc(s) {
    return String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  let toastTimer = null;
  function toast(text, isErr = false) {
    const t = $('#toast');
    t.textContent = text;
    t.className = 'toast' + (isErr ? ' err' : '');
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, isErr ? 6000 : 2800);
  }

  function getPath(obj, path) {
    return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
  }

  function patchFor(path, value) {
    const keys = path.split('.');
    const root = {};
    let o = root;
    keys.forEach((k, i) => {
      if (i === keys.length - 1) o[k] = value;
      else { o[k] = {}; o = o[k]; }
    });
    return root;
  }

  async function saveSetting(path, value) {
    try {
      settings = await api.settings.set(patchFor(path, value));
      afterSettings();
    } catch (e) {
      toast(e.message, true);
    }
  }

  // ================================================================== tabs
  function showTab(name) {
    $$('.tab').forEach((b) => b.classList.toggle('active', b.dataset.tab === name));
    $$('.panel').forEach((p) => p.classList.toggle('active', p.id === `tab-${name}`));
    if (name === 'chat') setTimeout(() => $('#input').focus(), 30);
    if (name === 'reminders') loadReminders();
    if (name === 'memory') loadMemory();
  }
  $$('.tab').forEach((b) => b.addEventListener('click', () => showTab(b.dataset.tab)));
  api.ui.onNavigate((tab) => showTab(tab || 'chat'));

  // ================================================================== status line
  let busy = false;
  function updateStatus() {
    if (!settings) return;
    const p = settings.provider;
    const model = settings.models?.[p] || '';
    const hasKey = secretStatus[p]?.set;
    const el = $('#status');
    el.classList.toggle('busy', busy);
    if (busy) el.textContent = 'печатает…';
    else if (!hasKey) el.textContent = `нет ключа ${PROVIDER_NAMES[p]} — открой настройки`;
    else el.textContent = `${PROVIDER_NAMES[p]} · ${model}`;
  }

  // ================================================================== chat
  const messagesEl = $('#messages');
  const input = $('#input');
  let attachments = []; // [{mime,data}]
  const live = new Map(); // turnId -> {el, text, tools}
  let stickToBottom = true;

  messagesEl.addEventListener('scroll', () => {
    stickToBottom = messagesEl.scrollHeight - messagesEl.scrollTop - messagesEl.clientHeight < 60;
  });

  function scrollDown(force) {
    if (force || stickToBottom) messagesEl.scrollTop = messagesEl.scrollHeight;
  }

  function timeStr(ts) {
    const d = ts ? new Date(ts) : new Date();
    return d.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
  }

  function hideEmpty() {
    const e = $('#chatEmpty');
    if (e) e.hidden = true;
  }

  function addUser(text, { hasImage = false, imageData = null, ts } = {}) {
    hideEmpty();
    const el = document.createElement('div');
    el.className = 'msg user';
    el.innerHTML = `<div class="body">
      ${imageData ? `<img class="attached" src="data:image/jpeg;base64,${imageData}" alt="">` : ''}
      <div class="bubble">${esc(text)}${hasImage && !imageData ? '<div class="attach-note">📎 изображение</div>' : ''}</div>
      <div class="meta">${timeStr(ts)}</div></div>`;
    messagesEl.appendChild(el);
    scrollDown(true);
    return el;
  }

  function addVera({ text = '', tools = [], ts, typing = false, error = false } = {}) {
    hideEmpty();
    const el = document.createElement('div');
    el.className = 'msg vera' + (error ? ' error' : '');
    el.innerHTML = `<img class="avatar" src="assets/icon.png" alt="">
      <div class="body"><div class="tools"></div><div class="bubble md"></div><div class="meta">${timeStr(ts)}</div></div>`;
    messagesEl.appendChild(el);
    renderVera(el, text, tools, typing);
    scrollDown(true);
    return el;
  }

  function renderVera(el, text, tools, typing) {
    const toolsEl = $('.tools', el);
    toolsEl.innerHTML = tools.map((t) => {
      const cls = t.status === 'run' ? 'run' : (t.isError ? 'err' : '');
      const label = t.status === 'run' ? `${t.name}…` : (t.label || t.name);
      return `<span class="tool-chip ${cls}" title="${esc(label)}">${esc(label)}</span>`;
    }).join('');
    toolsEl.hidden = !tools.length;
    const b = $('.bubble', el);
    if (text) b.innerHTML = window.VeraMarkdown.render(text);
    else if (typing) b.innerHTML = '<span class="typing"><i></i><i></i><i></i></span>';
    else b.innerHTML = '';
    b.hidden = !text && !typing;
  }

  messagesEl.addEventListener('click', (e) => {
    const a = e.target.closest('a[data-ext]');
    if (a) {
      e.preventDefault();
      api.app.openExternal(a.href);
      return;
    }
    const copy = e.target.closest('.codeblock .copy');
    if (copy) {
      const code = copy.parentElement.querySelector('code').textContent;
      navigator.clipboard.writeText(code).then(() => { copy.textContent = 'Скопировано'; setTimeout(() => { copy.textContent = 'Копировать'; }, 1500); });
    }
    const sug = e.target.closest('[data-suggest]');
    if (sug) {
      input.value = sug.dataset.suggest;
      send();
    }
  });

  async function loadHistory() {
    const hist = await api.chat.history();
    $$('.msg', messagesEl).forEach((m) => m.remove());
    for (const m of hist) {
      if (m.role === 'user') addUser(m.text, { hasImage: m.hasImage, ts: m.ts });
      else addVera({ text: m.text, tools: (m.tools || []).map((t) => ({ ...t, status: 'done' })), ts: m.ts });
    }
    $('#chatEmpty').hidden = hist.length > 0;
    scrollDown(true);
  }

  let rafPending = new Set();
  function scheduleRender(turnId) {
    if (rafPending.has(turnId)) return;
    rafPending.add(turnId);
    requestAnimationFrame(() => {
      rafPending.delete(turnId);
      const t = live.get(turnId);
      if (!t) return;
      renderVera(t.el, t.text, t.tools, !t.text);
      scrollDown(false);
    });
  }

  api.chat.onEvent((e) => {
    if (e.type === 'turn-start') {
      busy = true;
      setSendMode();
      updateStatus();
      if (!e.user.hidden) addUser(e.user.text, { hasImage: e.user.hasImage, imageData: pendingPreview });
      pendingPreview = null;
      const el = addVera({ typing: true });
      live.set(e.turnId, { el, text: '', tools: [], lastStep: null });
    } else if (e.type === 'delta') {
      const t = live.get(e.turnId);
      if (!t) return;
      if (t.lastStep != null && t.lastStep !== e.step && t.text) t.text = t.text.trimEnd() + '\n\n';
      t.lastStep = e.step;
      t.text += e.delta;
      scheduleRender(e.turnId);
    } else if (e.type === 'tool') {
      const t = live.get(e.turnId);
      if (!t) return;
      const ex = t.tools.find((x) => x.id === e.id);
      if (ex) Object.assign(ex, e); else t.tools.push({ ...e });
      scheduleRender(e.turnId);
      if (e.status === 'done' && (e.name.includes('reminder'))) loadReminders();
    } else if (e.type === 'turn-end') {
      const t = live.get(e.turnId);
      if (t) {
        if (!t.text && e.text) t.text = e.text;
        renderVera(t.el, t.text || (t.tools.length ? '' : '…'), t.tools, false);
        live.delete(e.turnId);
      }
      busy = false;
      setSendMode();
      updateStatus();
      scrollDown(false);
    } else if (e.type === 'turn-error') {
      const t = live.get(e.turnId);
      if (t) {
        if (e.aborted) {
          renderVera(t.el, (t.text || '') + (t.text ? ' …' : '*(остановлено)*'), t.tools, false);
        } else {
          t.el.classList.add('error');
          renderVera(t.el, (t.text ? t.text + '\n\n' : '') + '⚠️ ' + e.message, t.tools, false);
        }
        live.delete(e.turnId);
      }
      busy = false;
      setSendMode();
      updateStatus();
    }
  });
  api.chat.onCleared(() => loadHistory());

  function setSendMode() {
    const b = $('#btnSend');
    b.classList.toggle('stop', busy);
    b.innerHTML = busy ? '<svg><use href="#i-stop"/></svg>' : '<svg><use href="#i-send"/></svg>';
    b.title = busy ? 'Остановить' : 'Отправить';
  }

  let pendingPreview = null;
  async function send() {
    if (busy) {
      api.chat.stop();
      return;
    }
    const text = input.value.trim();
    if (!text && !attachments.length) return;
    if (!secretStatus[settings.provider]?.set) {
      toast(`Сначала добавь ключ ${PROVIDER_NAMES[settings.provider]} в настройках`, true);
      showTab('settings');
      return;
    }
    const images = attachments.slice();
    pendingPreview = images[0]?.data || null;
    input.value = '';
    autosize();
    attachments = [];
    renderAttachments();
    try {
      await api.chat.send({ text, images, source: 'chat' });
    } catch (e) {
      toast(e.message, true);
    }
  }

  function autosize() {
    input.style.height = 'auto';
    input.style.height = Math.min(160, input.scrollHeight + 2) + 'px';
  }

  input.addEventListener('input', autosize);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      send();
    }
  });
  input.addEventListener('paste', (e) => {
    const items = Array.from(e.clipboardData?.items || []);
    const img = items.find((i) => i.type.startsWith('image/'));
    if (!img) return;
    e.preventDefault();
    const file = img.getAsFile();
    const reader = new FileReader();
    reader.onload = () => {
      const [head, data] = String(reader.result).split(',');
      const mime = head.match(/data:(.*?);/)?.[1] || 'image/png';
      attachments.push({ mime, data });
      renderAttachments();
    };
    reader.readAsDataURL(file);
  });

  function renderAttachments() {
    const box = $('#attachments');
    box.hidden = !attachments.length;
    box.innerHTML = attachments.map((a, i) => `<div class="attachment"><img src="data:${a.mime};base64,${a.data}" alt="">
      <button data-rm="${i}" title="Убрать"><svg><use href="#i-x"/></svg></button></div>`).join('');
  }
  $('#attachments').addEventListener('click', (e) => {
    const b = e.target.closest('[data-rm]');
    if (!b) return;
    attachments.splice(Number(b.dataset.rm), 1);
    renderAttachments();
  });

  $('#btnSend').addEventListener('click', send);
  $('#btnShot').addEventListener('click', async () => {
    try {
      const shot = await api.screen.capture();
      attachments = [{ mime: shot.mime, data: shot.data }];
      renderAttachments();
      if (!input.value.trim()) input.value = 'Что у меня на экране?';
      input.focus();
    } catch (e) {
      toast(e.message, true);
    }
  });
  $('#btnMic').addEventListener('click', () => api.pet.command('listen'));
  $('#btnNewChat').addEventListener('click', async () => {
    if (!confirm('Начать новый диалог? Текущая переписка будет очищена (память и напоминания останутся).')) return;
    await api.chat.clear();
  });

  // ================================================================== reminders
  function pad(n) { return String(n).padStart(2, '0'); }
  function toLocalInput(ts) {
    const d = new Date(ts);
    return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
  }
  function relative(ts) {
    let diff = Math.round((ts - Date.now()) / 60000);
    const past = diff < 0;
    diff = Math.abs(diff);
    let s;
    if (diff < 1) s = 'меньше минуты';
    else if (diff < 60) s = `${diff} мин`;
    else if (diff < 1440) s = `${Math.floor(diff / 60)} ч${diff % 60 ? ' ' + (diff % 60) + ' мин' : ''}`;
    else s = `${Math.floor(diff / 1440)} дн${Math.floor((diff % 1440) / 60) ? ' ' + Math.floor((diff % 1440) / 60) + ' ч' : ''}`;
    return past ? `${s} назад` : `через ${s}`;
  }
  function whenStr(ts) {
    const d = new Date(ts);
    const today = new Date();
    const tomorrow = new Date(); tomorrow.setDate(today.getDate() + 1);
    const time = `${pad(d.getHours())}:${pad(d.getMinutes())}`;
    if (d.toDateString() === today.toDateString()) return `сегодня ${time}`;
    if (d.toDateString() === tomorrow.toDateString()) return `завтра ${time}`;
    return d.toLocaleDateString('ru-RU', { day: 'numeric', month: 'short', weekday: 'short' }) + ' ' + time;
  }

  function resetRemWhen() {
    const d = new Date(Date.now() + 60 * 60000);
    d.setMinutes(Math.ceil(d.getMinutes() / 5) * 5, 0, 0);
    $('#remWhen').value = toLocalInput(d.getTime());
  }

  let reminderCache = [];
  async function loadReminders() {
    try {
      reminderCache = await api.reminders.list();
    } catch (e) { toast(e.message, true); return; }
    renderReminders();
  }

  function renderReminders() {
    const active = reminderCache.filter((r) => !r.done);
    const done = reminderCache.filter((r) => r.done).sort((a, b) => (b.firedAt || 0) - (a.firedAt || 0));
    $('#remCount').textContent = active.length ? `(${active.length})` : '';
    $('#remDoneCount').textContent = done.length ? `(${done.length})` : '';
    const badge = $('#remBadge');
    badge.hidden = !active.length;
    badge.textContent = String(active.length);
    const item = (r) => `<div class="item ${r.done ? 'done' : ''} ${!r.done && r.due - Date.now() < 15 * 60000 ? 'soon' : ''}" data-id="${r.id}">
      <div class="grow">
        <div class="title">${esc(r.text)}</div>
        <div class="sub-line">
          <span class="when">${esc(whenStr(r.due))}</span>
          <span>${r.done ? 'сработало' : esc(relative(r.due))}</span>
          ${r.repeat !== 'none' ? `<span class="tag"><svg><use href="#i-repeat"/></svg>${esc(r.repeatText)}</span>` : ''}
          ${r.source === 'ai' ? '<span class="tag">от Веры</span>' : ''}
        </div>
      </div>
      <div class="actions">
        ${r.done ? `<button class="icon-btn" data-act="again" title="Повторить через 10 минут"><svg><use href="#i-clock"/></svg></button>` : ''}
        <button class="icon-btn" data-act="delete" title="Удалить"><svg><use href="#i-trash"/></svg></button>
      </div></div>`;
    $('#remList').innerHTML = active.map(item).join('') || '<div class="list-empty">Пусто. Скажи Вере «напомни через 10 минут…» 😉</div>';
    $('#remDoneList').innerHTML = done.map(item).join('') || '<div class="list-empty">Ничего не срабатывало</div>';
  }

  $('#tab-reminders').addEventListener('click', async (e) => {
    const quick = e.target.closest('[data-quick]');
    if (quick) {
      const q = quick.dataset.quick;
      let t;
      if (q === 'tomorrow9') { const d = new Date(); d.setDate(d.getDate() + 1); d.setHours(9, 0, 0, 0); t = d.getTime(); }
      else t = Date.now() + Number(q) * 60000;
      $('#remWhen').value = toLocalInput(t);
      return;
    }
    const btn = e.target.closest('[data-act]');
    if (!btn) return;
    const id = btn.closest('[data-id]').dataset.id;
    try {
      if (btn.dataset.act === 'delete') await api.reminders.remove(id);
      if (btn.dataset.act === 'again') await api.reminders.snooze(id, 10);
      loadReminders();
    } catch (err) { toast(err.message, true); }
  });

  $('#remAdd').addEventListener('click', async () => {
    const text = $('#remText').value.trim();
    const when = $('#remWhen').value;
    if (!text) { toast('Напиши, о чём напомнить', true); $('#remText').focus(); return; }
    if (!when) { toast('Выбери время', true); return; }
    try {
      const r = await api.reminders.add({ text, due: new Date(when).getTime(), repeat: $('#remRepeat').value });
      $('#remText').value = '';
      resetRemWhen();
      toast(`Готово: ${r.dueText}`);
      loadReminders();
    } catch (e) { toast(e.message, true); }
  });
  $('#remText').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('#remAdd').click(); });
  api.reminders.onChange(() => loadReminders());
  setInterval(() => { if (reminderCache.length) renderReminders(); }, 30000);

  // ================================================================== memory
  async function loadMemory() {
    let list;
    try { list = await api.memory.list(); } catch (e) { toast(e.message, true); return; }
    $('#memCount').textContent = list.length ? `(${list.length})` : '';
    $('#memList').innerHTML = list.slice().reverse().map((m) => `<div class="item" data-id="${m.id}">
      <div class="grow"><div class="title editable">${esc(m.text)}</div>
        <div class="sub-line"><span>${new Date(m.created).toLocaleDateString('ru-RU')}</span>${m.source === 'ai' ? '<span class="tag">запомнила сама</span>' : ''}</div></div>
      <div class="actions">
        <button class="icon-btn" data-act="edit" title="Изменить"><svg><use href="#i-edit"/></svg></button>
        <button class="icon-btn" data-act="delete" title="Забыть"><svg><use href="#i-trash"/></svg></button>
      </div></div>`).join('') || '<div class="list-empty">Вера пока ничего о тебе не знает. Расскажи ей о себе!</div>';
  }

  $('#memList').addEventListener('click', async (e) => {
    const btn = e.target.closest('[data-act]');
    if (!btn) return;
    const row = btn.closest('[data-id]');
    const id = row.dataset.id;
    if (btn.dataset.act === 'delete') {
      await api.memory.remove(id).catch((err) => toast(err.message, true));
      loadMemory();
    } else if (btn.dataset.act === 'edit') {
      const t = $('.editable', row);
      if (t.isContentEditable) {
        t.contentEditable = 'false';
        await api.memory.update(id, t.textContent).catch((err) => toast(err.message, true));
        loadMemory();
      } else {
        t.contentEditable = 'true';
        t.focus();
        document.getSelection().selectAllChildren(t);
        t.onkeydown = (ev) => { if (ev.key === 'Enter') { ev.preventDefault(); btn.click(); } };
      }
    }
  });
  $('#memAdd').addEventListener('click', async () => {
    const t = $('#memText').value.trim();
    if (!t) return;
    try { await api.memory.add(t); $('#memText').value = ''; loadMemory(); } catch (e) { toast(e.message, true); }
  });
  $('#memText').addEventListener('keydown', (e) => { if (e.key === 'Enter') $('#memAdd').click(); });
  $('#memClear').addEventListener('click', async () => {
    if (!confirm('Вера забудет всё, что знает о тебе. Точно?')) return;
    await api.memory.clear();
    loadMemory();
  });
  api.memory.onChange(() => { if ($('#tab-memory').classList.contains('active')) loadMemory(); });

  // ================================================================== settings: generic binding
  function fillForm() {
    for (const el of $$('[data-key]')) {
      if (document.activeElement === el && el.type !== 'checkbox' && el.type !== 'range') continue;
      const v = getPath(settings, el.dataset.key);
      if (el.type === 'checkbox') el.checked = !!v;
      else if (el.classList.contains('hotkey')) el.value = prettyAccel(v || '');
      else if (v != null) el.value = String(v);
    }
    for (const out of $$('output[data-out]')) {
      const v = getPath(settings, out.dataset.out);
      const n = Number(v);
      out.textContent = (Number.isFinite(n) && !Number.isInteger(n) ? n.toFixed(2) : v) + (out.dataset.suffix || '');
    }
    for (const el of $$('[data-show]')) {
      const [path, val] = el.dataset.show.split('=');
      el.hidden = String(getPath(settings, path)) !== val;
    }
    // provider + model
    $$('#providerSeg button').forEach((b) => b.classList.toggle('active', b.dataset.provider === settings.provider));
    if (document.activeElement !== $('#modelInput')) $('#modelInput').value = settings.models?.[settings.provider] || '';
    $('#modelHint').textContent = MODEL_HINTS[settings.provider] || '';
    // personas
    const ps = $('#personaSelect');
    if (!ps.options.length && info?.personas) {
      ps.innerHTML = Object.entries(info.personas).map(([k, v]) => `<option value="${k}">${esc(v.label)}</option>`).join('');
      ps.value = settings.persona;
    }
    // hotkey errors
    const he = $('#hotkeyErrors');
    he.hidden = !settings._hotkeyErrors?.length;
    he.textContent = settings._hotkeyErrors?.length ? 'Не удалось назначить: ' + settings._hotkeyErrors.join('; ') : '';
    // theme
    const theme = settings.theme === 'auto' ? (matchMedia('(prefers-color-scheme: light)').matches ? 'light' : 'dark') : settings.theme;
    if (document.documentElement.dataset.theme !== theme) {
      document.documentElement.dataset.theme = theme;
      api.app.setTheme(theme).catch(() => {});
    }
    renderApps();
  }

  function afterSettings() {
    fillForm();
    updateStatus();
  }

  document.addEventListener('change', (e) => {
    const el = e.target.closest('[data-key]');
    if (!el || el.classList.contains('hotkey')) return;
    let v;
    if (el.type === 'checkbox') v = el.checked;
    else if (el.dataset.type === 'number') v = Number(el.value);
    else v = el.value;
    saveSetting(el.dataset.key, v);
  });
  document.addEventListener('input', (e) => {
    const el = e.target.closest('input[type="range"][data-key]');
    if (!el) return;
    const out = $(`output[data-out="${el.dataset.key}"]`);
    if (out) {
      const n = Number(el.value);
      out.textContent = (Number.isInteger(n) ? n : n.toFixed(2)) + (out.dataset.suffix || '');
    }
  });

  // ---------------------------------------------------------------- provider / model
  const MODEL_HINTS = {
    openrouter: 'Любая модель OpenRouter с поддержкой tools, например openai/gpt-5-mini, anthropic/…, google/gemini-…',
    anthropic: 'Например claude-opus-5-5 (умнее всех), claude-sonnet-5-5 (быстрее), claude-haiku-5-5 (дешевле).',
    openai: 'Например gpt-5-mini, gpt-5, gpt-4.1-mini.',
  };
  $('#providerSeg').addEventListener('click', (e) => {
    const b = e.target.closest('[data-provider]');
    if (!b) return;
    $('#modelList').innerHTML = '';
    saveSetting('provider', b.dataset.provider);
  });
  $('#modelInput').addEventListener('change', () => {
    const v = $('#modelInput').value.trim();
    if (v) saveSetting(`models.${settings.provider}`, v);
  });
  $('#modelRefresh').addEventListener('click', async () => {
    const btn = $('#modelRefresh');
    btn.disabled = true;
    try {
      const list = await api.models.list(settings.provider);
      const rows = settings.provider === 'openrouter' ? list.filter((m) => m.tools) : list;
      $('#modelList').innerHTML = rows.map((m) => `<option value="${esc(m.id)}">${esc(m.name)}${m.vision ? ' · 👁' : ''}</option>`).join('');
      toast(`Загружено моделей: ${rows.length}. Начни вводить название.`);
      $('#modelInput').focus();
    } catch (e) {
      toast(e.message, true);
    } finally {
      btn.disabled = false;
    }
  });

  // ---------------------------------------------------------------- keys
  const KEYS = [
    { id: 'openrouter', name: 'OpenRouter', url: 'https://openrouter.ai/keys', hint: 'чат — сотни моделей по одному ключу' },
    { id: 'anthropic', name: 'Anthropic', url: 'https://console.anthropic.com/settings/keys', hint: 'чат — Claude' },
    { id: 'openai', name: 'OpenAI', url: 'https://platform.openai.com/api-keys', hint: 'чат, голос и распознавание речи' },
    { id: 'groq', name: 'Groq', url: 'https://console.groq.com/keys', hint: 'необязательно — быстрое распознавание речи' },
    { id: 'elevenlabs', name: 'ElevenLabs', url: 'https://elevenlabs.io/app/settings/api-keys', hint: 'необязательно — голоса ElevenLabs' },
  ];

  function renderKeys() {
    $('#keys').innerHTML = KEYS.map((k) => {
      const st = secretStatus[k.id] || {};
      return `<div class="key-row" data-key-id="${k.id}">
        <div class="key-head"><span class="dot ${st.set ? 'on' : ''}"></span>${k.name}<span class="muted small">${esc(k.hint)}</span>
          <a href="${k.url}" data-ext="1">получить ключ ↗</a></div>
        <div class="row">
          <input type="password" placeholder="${st.set ? 'сохранён: ' + esc(st.hint) : 'вставь ключ'}" spellcheck="false" autocomplete="off">
          <button class="ghost small" data-k="save">Сохранить</button>
          <button class="ghost small" data-k="test" ${st.set ? '' : 'disabled'}>Проверить</button>
          ${st.set ? '<button class="icon-btn" data-k="del" title="Удалить ключ"><svg><use href="#i-trash"/></svg></button>' : ''}
        </div>
        <div class="status small"></div>
      </div>`;
    }).join('');
  }

  $('#keys').addEventListener('click', async (e) => {
    const a = e.target.closest('a[data-ext]');
    if (a) { e.preventDefault(); api.app.openExternal(a.href); return; }
    const b = e.target.closest('[data-k]');
    if (!b) return;
    const row = b.closest('[data-key-id]');
    const id = row.dataset.keyId;
    const status = $('.status', row);
    const inp = $('input', row);
    try {
      if (b.dataset.k === 'save') {
        if (!inp.value.trim()) { toast('Вставь ключ в поле', true); return; }
        secretStatus = await api.secrets.set(id, inp.value.trim());
        renderKeys();
        updateStatus();
        const row2 = $(`[data-key-id="${id}"] .status`);
        row2.textContent = 'Сохранено. Проверяю…';
        try {
          const r = await api.secrets.test(id);
          row2.textContent = '✓ ' + r; row2.className = 'status small ok';
        } catch (err) {
          row2.textContent = '✗ ' + err.message; row2.className = 'status small err';
        }
      } else if (b.dataset.k === 'test') {
        status.textContent = 'Проверяю…'; status.className = 'status small';
        const r = await api.secrets.test(id);
        status.textContent = '✓ ' + r; status.className = 'status small ok';
      } else if (b.dataset.k === 'del') {
        if (!confirm('Удалить ключ?')) return;
        secretStatus = await api.secrets.set(id, '');
        renderKeys();
        updateStatus();
      }
    } catch (err) {
      status.textContent = '✗ ' + err.message;
      status.className = 'status small err';
    }
  });
  $('#keys').addEventListener('keydown', (e) => {
    if (e.key === 'Enter' && e.target.matches('input')) $('[data-k="save"]', e.target.closest('.key-row')).click();
  });

  // ---------------------------------------------------------------- voice
  $('#ttsFromPersona').addEventListener('click', (e) => {
    e.preventDefault();
    const t = info?.personas?.[settings.persona]?.tts;
    if (t) saveSetting('tts.openaiInstructions', t);
  });
  $('#ttsTest').addEventListener('click', () => {
    if (settings.tts.engine === 'off') { toast('Голос выключен — выбери движок', true); return; }
    api.pet.command('speak', $('#ttsTestText').value);
  });
  $('#elevenRefresh').addEventListener('click', async () => {
    try {
      const voices = await api.voice.elevenVoices();
      const sel = $('#elevenVoice');
      sel.innerHTML = '<option value="">(по умолчанию)</option>' + voices.map((v) => `<option value="${esc(v.id)}">${esc(v.name)}${v.labels?.gender ? ' · ' + esc(v.labels.gender) : ''}</option>`).join('');
      sel.value = settings.tts.elevenVoiceId || '';
      toast(`Голосов: ${voices.length}`);
    } catch (e) { toast(e.message, true); }
  });
  function loadSystemVoices() {
    const voices = speechSynthesis.getVoices();
    const sel = $('#systemVoice');
    const cur = settings?.tts?.systemVoice || '';
    sel.innerHTML = '<option value="">(авто, русский)</option>' + voices.map((v) => `<option value="${esc(v.name)}">${esc(v.name)} (${esc(v.lang)})</option>`).join('');
    sel.value = cur;
  }
  speechSynthesis.onvoiceschanged = loadSystemVoices;

  async function loadMics(askPermission) {
    try {
      if (askPermission) {
        const s = await navigator.mediaDevices.getUserMedia({ audio: true });
        s.getTracks().forEach((t) => t.stop());
      }
      const devs = (await navigator.mediaDevices.enumerateDevices()).filter((d) => d.kind === 'audioinput');
      const sel = $('#micSelect');
      sel.innerHTML = '<option value="">По умолчанию</option>' + devs.filter((d) => d.deviceId && d.deviceId !== 'default')
        .map((d, i) => `<option value="${esc(d.deviceId)}">${esc(d.label || 'Микрофон ' + (i + 1))}</option>`).join('');
      sel.value = settings?.voice?.micDeviceId || '';
    } catch (e) {
      if (askPermission) toast('Нет доступа к микрофону: ' + e.message, true);
    }
  }
  $('#micRefresh').addEventListener('click', () => loadMics(true));

  // ---------------------------------------------------------------- hotkeys
  function prettyAccel(a) {
    return a.replace(/CommandOrControl/g, 'Ctrl').replace(/\+/g, ' + ');
  }
  const KEYMAP = { ' ': 'Space', ArrowUp: 'Up', ArrowDown: 'Down', ArrowLeft: 'Left', ArrowRight: 'Right', Escape: 'Esc' };
  for (const el of $$('.hotkey')) {
    el.addEventListener('focus', () => { el.classList.add('recording'); el.value = 'Нажми сочетание…'; api.app.hotkeysPause(true); });
    el.addEventListener('blur', () => { el.classList.remove('recording'); api.app.hotkeysPause(false); fillForm(); });
    el.addEventListener('keydown', (e) => {
      e.preventDefault();
      if (e.key === 'Backspace' || e.key === 'Delete') { saveSetting(el.dataset.key, ''); el.blur(); return; }
      if (['Control', 'Shift', 'Alt', 'Meta'].includes(e.key)) return;
      const mods = [];
      if (e.ctrlKey) mods.push('CommandOrControl');
      if (e.altKey) mods.push('Alt');
      if (e.shiftKey) mods.push('Shift');
      if (e.metaKey) mods.push('Super');
      let key = KEYMAP[e.key] || e.key;
      if (/^Key[A-Z]$/.test(e.code)) key = e.code.slice(3);
      else if (/^Digit\d$/.test(e.code)) key = e.code.slice(5);
      else if (/^F\d{1,2}$/.test(e.key)) key = e.key;
      else if (key.length === 1) key = key.toUpperCase();
      if (!mods.length && !/^F\d{1,2}$/.test(key)) { el.value = 'Нужен Ctrl / Alt / Shift'; return; }
      saveSetting(el.dataset.key, [...mods, key].join('+'));
      el.blur();
    });
  }

  // ---------------------------------------------------------------- apps
  let pickedPath = '';
  function renderApps() {
    const list = settings.appShortcuts || [];
    $('#appsList').innerHTML = list.map((a, i) => `<div class="item"><div class="grow"><div class="title">${esc(a.name)}</div>
      <div class="sub-line">${esc(a.path)}</div></div>
      <div class="actions"><button class="icon-btn" data-app-rm="${i}" title="Удалить"><svg><use href="#i-trash"/></svg></button></div></div>`).join('');
  }
  $('#appPick').addEventListener('click', async () => {
    const p = await api.app.pickFile().catch((e) => toast(e.message, true));
    if (!p) return;
    pickedPath = p;
    const name = $('#appName').value.trim() || p.split(/[\\/]/).pop().replace(/\.(exe|lnk|url|bat|cmd)$/i, '');
    const list = [...(settings.appShortcuts || []), { name, path: p }];
    await saveSetting('appShortcuts', list);
    $('#appName').value = '';
    $('#appPath').textContent = `Добавлено: ${name}`;
  });
  $('#appsList').addEventListener('click', async (e) => {
    const b = e.target.closest('[data-app-rm]');
    if (!b) return;
    const list = (settings.appShortcuts || []).filter((_, i) => i !== Number(b.dataset.appRm));
    saveSetting('appShortcuts', list);
  });

  // ---------------------------------------------------------------- system
  $('#openData').addEventListener('click', () => api.app.openDataFolder());
  $('#quitApp').addEventListener('click', () => { if (confirm('Выйти из Веры? Напоминания не будут срабатывать, пока она выключена.')) api.app.quit(); });
  $$('[data-win]').forEach((b) => b.addEventListener('click', () => api.app.winControl(b.dataset.win)));
  matchMedia('(prefers-color-scheme: light)').addEventListener('change', () => fillForm());

  // ================================================================== init
  async function init() {
    info = await api.app.info();
    settings = await api.settings.get();
    secretStatus = await api.secrets.status();
    if (info.platform !== 'win32') $('#winControls').hidden = false;
    $('#aboutLine').textContent = `Вера v${info.version} · данные: ${info.dataDir}`;
    const anyKey = Object.values(secretStatus).some((s) => s.set);
    $('#welcomeCard').hidden = anyKey;
    renderKeys();
    fillForm();
    updateStatus();
    resetRemWhen();
    loadSystemVoices();
    loadMics(false);
    await loadHistory();
    loadReminders();
    busy = await api.chat.busy();
    setSendMode();
    api.settings.onChange((s) => { settings = s; afterSettings(); });
    api.secrets.onChange((s) => { secretStatus = s; renderKeys(); updateStatus(); $('#welcomeCard').hidden = Object.values(s).some((x) => x.set); });
    autosize();
    input.focus();
  }

  init().catch((e) => toast(e.message, true));
})();
