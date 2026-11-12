/* Окно-питомец: чиби-Вера, мимика, пузырь с речью, голос и микрофон. */
(function () {
  'use strict';
  const api = window.vera;
  const { Listener, Speaker, Sfx } = window.VeraAudio;

  // ------------------------------------------------------------------ sprite
  const LAYERS = {
    base: ['idle', 'wave', 'giggle', 'think'],
    hand: ['idle', 'wave', 'giggle', 'think'],
    eyes: ['normal', 'smug', 'happy', 'closed', 'surprised', 'angry', 'side', 'up'],
    mouth: ['smirk', 'cat', 'flat', 'open', 'open_fang', 'laugh', 'o', 'pout', 'wobbly', 'small_open'],
    blush: ['0', '1', '2'],
  };
  const EMO = {
    neutral: { eyes: 'normal', mouth: 'smirk', talk: 'open_fang', blush: 0, pose: 'idle' },
    smug: { eyes: 'smug', mouth: 'smirk', talk: 'open_fang', blush: 1, pose: 'giggle' },
    happy: { eyes: 'happy', mouth: 'cat', talk: 'laugh', blush: 1, pose: 'wave' },
    laugh: { eyes: 'happy', mouth: 'laugh', talk: 'open', blush: 1, pose: 'giggle' },
    think: { eyes: 'up', mouth: 'flat', talk: 'small_open', blush: 0, pose: 'think' },
    surprised: { eyes: 'surprised', mouth: 'o', talk: 'small_open', blush: 0, pose: 'idle' },
    angry: { eyes: 'angry', mouth: 'pout', talk: 'open_fang', blush: 2, pose: 'idle' },
    shy: { eyes: 'side', mouth: 'wobbly', talk: 'small_open', blush: 2, pose: 'idle' },
    sad: { eyes: 'side', mouth: 'pout', talk: 'small_open', blush: 0, pose: 'idle' },
    sleepy: { eyes: 'closed', mouth: 'cat', talk: 'small_open', blush: 0, pose: 'idle' },
  };
  const BLINKABLE = new Set(['normal', 'smug', 'side', 'up', 'angry']);

  const images = {};
  function loadImages() {
    const jobs = [];
    for (const [kind, names] of Object.entries(LAYERS)) {
      for (const n of names) {
        const img = new Image();
        img.src = `assets/vera/${kind}_${n}.png`;
        images[`${kind}_${n}`] = img;
        jobs.push(img.decode().catch(() => null));
      }
    }
    const front = new Image();
    front.src = 'assets/vera/front.png';
    images.front = front;
    jobs.push(front.decode().catch(() => null));
    return Promise.all(jobs);
  }

  const canvas = document.getElementById('sprite');
  const g = canvas.getContext('2d', { willReadFrequently: true });
  g.imageSmoothingEnabled = false;
  const veraEl = document.getElementById('vera');
  const fxEl = document.getElementById('fx');

  // ------------------------------------------------------------------ state
  let settings = null;
  const st = {
    emotion: 'neutral',
    emotionUntil: 0,
    mode: 'idle', // idle | listening | recording | processing | thinking | talking | sleeping
    blink: false,
    mouthLevel: 0,
    textFlapUntil: 0,
    dragging: false,
    lastInteraction: Date.now(),
    lastFrameKey: '',
  };

  function setEmotion(e, ms = 9000) {
    if (!EMO[e]) return;
    st.emotion = e;
    st.emotionUntil = Date.now() + ms;
  }

  function currentFrame() {
    const now = Date.now();
    const emoName = now < st.emotionUntil ? st.emotion : 'neutral';
    const emo = EMO[emoName] || EMO.neutral;
    let { eyes, mouth, blush, pose } = emo;
    let talkMouth = emo.talk;

    if (st.mode === 'thinking') { pose = 'think'; eyes = 'up'; mouth = 'flat'; }
    if (st.mode === 'listening' || st.mode === 'recording') { pose = 'idle'; eyes = 'normal'; mouth = st.mode === 'recording' ? 'cat' : 'smirk'; }
    if (st.mode === 'processing') { pose = 'think'; eyes = 'up'; mouth = 'cat'; }
    if (st.mode === 'sleeping') { pose = 'idle'; eyes = 'closed'; mouth = 'cat'; blush = 0; }
    if (st.dragging) { pose = 'wave'; eyes = 'surprised'; mouth = 'o'; }

    const talking = st.mouthLevel > 0.18 || now < st.textFlapUntil;
    if (talking || speaker.active) {
      if (pose === 'giggle') pose = 'idle';
      if (talking) {
        const open = st.mouthLevel > 0.18 ? st.mouthLevel > 0.35 || (now >> 7) % 2 === 0 : (now >> 7) % 2 === 0;
        mouth = open ? talkMouth : (mouth === 'laugh' ? 'cat' : mouth);
      }
    }
    if (st.blink && BLINKABLE.has(eyes)) eyes = 'closed';
    return { pose, eyes, mouth, blush };
  }

  function draw(force) {
    const f = currentFrame();
    const key = `${f.pose}|${f.eyes}|${f.mouth}|${f.blush}`;
    if (!force && key === st.lastFrameKey) return;
    st.lastFrameKey = key;
    g.clearRect(0, 0, 64, 72);
    const L = (k) => images[k];
    for (const k of [`base_${f.pose}`, `eyes_${f.eyes}`, `mouth_${f.mouth}`, `blush_${f.blush}`, 'front', `hand_${f.pose}`]) {
      const img = L(k);
      if (img && img.complete && img.naturalWidth) g.drawImage(img, 0, 0);
    }
  }

  function loop() {
    draw(false);
    requestAnimationFrame(loop);
  }

  function scheduleBlink() {
    setTimeout(() => {
      st.blink = true;
      setTimeout(() => {
        st.blink = false;
        if (Math.random() < 0.18) { // двойное моргание
          setTimeout(() => { st.blink = true; setTimeout(() => { st.blink = false; }, 110); }, 160);
        }
      }, 120);
      scheduleBlink();
    }, 2200 + Math.random() * 4200);
  }

  function hop() {
    veraEl.classList.remove('hop');
    void veraEl.offsetWidth;
    veraEl.classList.add('hop');
    setTimeout(() => veraEl.classList.remove('hop'), 450);
  }

  function shake() {
    veraEl.classList.remove('shake');
    void veraEl.offsetWidth;
    veraEl.classList.add('shake');
    setTimeout(() => veraEl.classList.remove('shake'), 850);
  }

  function fx(text, cls = '', dx = 10, dy = 0) {
    const s = document.createElement('span');
    s.className = `fx ${cls}`;
    s.textContent = text;
    const scale = settings?.pet?.scale || 3;
    s.style.left = `${dx * scale}px`;
    s.style.top = `${dy * scale}px`;
    fxEl.appendChild(s);
    setTimeout(() => s.remove(), 2000);
  }

  // ------------------------------------------------------------------ bubble
  const bubble = document.getElementById('bubble');
  const bubbleText = document.getElementById('bubbleText');
  const bubbleHead = document.getElementById('bubbleHead');
  const bubbleActions = document.getElementById('bubbleActions');
  const levelsEl = document.getElementById('levels');
  let bubbleTimer = null;
  let bubbleSticky = false;
  let bubbleOpensChat = false;

  function showBubble({ text = '', head = '', html = null, actions = [], sticky = false, ms = null, opensChat = false }) {
    clearTimeout(bubbleTimer);
    bubbleHead.textContent = head;
    if (html != null) bubbleText.innerHTML = html;
    else bubbleText.textContent = text;
    bubbleActions.innerHTML = '';
    for (const a of actions) {
      const b = document.createElement('button');
      b.textContent = a.label;
      if (a.primary) b.className = 'primary';
      b.addEventListener('click', (e) => { e.stopPropagation(); a.onClick(); });
      bubbleActions.appendChild(b);
    }
    bubbleSticky = sticky;
    bubbleOpensChat = opensChat;
    bubble.classList.toggle('clickable', opensChat);
    bubble.classList.remove('hidden');
    if (!sticky) hideBubbleLater(ms);
    requestAnimationFrame(updateHitArea);
  }

  function hideBubbleLater(ms) {
    clearTimeout(bubbleTimer);
    const base = (settings?.pet?.bubbleSeconds || 9) * 1000;
    const len = bubbleText.textContent.length;
    bubbleTimer = setTimeout(hideBubble, ms ?? Math.min(base + len * 45, 40000));
  }

  function hideBubble() {
    clearTimeout(bubbleTimer);
    bubbleSticky = false;
    bubble.classList.add('hidden');
    levelsEl.classList.add('hidden');
  }

  bubble.addEventListener('click', () => {
    if (bubbleOpensChat) api.app.showMain('chat');
    else if (!bubbleSticky) hideBubble();
  });

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
  }

  function plainPreview(text, max = 230) {
    let t = String(text || '')
      .replace(/```[\s\S]*?```/g, '[код]')
      .replace(/```[\s\S]*$/g, '[код]')
      .replace(/\*\*|__|`/g, '')
      .replace(/^#+\s*/gm, '')
      .trim();
    let cut = false;
    if (t.length > max) { t = t.slice(0, max).replace(/\s+\S*$/, ''); cut = true; }
    return { text: t, cut };
  }

  // ------------------------------------------------------------------ speech
  const speaker = new Speaker({
    volume: () => settings?.tts?.volume ?? 0.9,
    rate: () => settings?.tts?.rate ?? 1,
    systemVoice: () => settings?.tts?.systemVoice || '',
    onLevel: (v) => { st.mouthLevel = v; },
    onStart: () => { listener.paused = true; },
    onEnd: () => {
      st.mouthLevel = 0;
      setTimeout(() => { if (!speaker.active) listener.paused = false; }, 600);
    },
    onError: (e) => {
      console.warn('tts', e);
      if (!ttsErrorShown) {
        ttsErrorShown = true;
        showBubble({ head: 'голос', text: String(e?.message || e), ms: 7000 });
      }
    },
  });
  let ttsErrorShown = false;

  function shouldSpeak(source) {
    const t = settings?.tts;
    if (!t || t.engine === 'off') return false;
    if (t.autoSpeak === 'always') return true;
    if (t.autoSpeak === 'never') return false;
    return source === 'voice';
  }

  /** Режет стрим на предложения для озвучки по мере генерации. */
  class Chunker {
    constructor(onChunk) { this.buf = ''; this.inCode = false; this.onChunk = onChunk; }
    push(d) {
      this.buf += d;
      for (;;) {
        const fence = this.buf.indexOf('```');
        if (this.inCode) {
          if (fence === -1) return;
          this.buf = this.buf.slice(fence + 3);
          this.inCode = false;
          continue;
        }
        const re = /([.!?…]+["»)\]]?\s+|\n+)/g;
        let m; let cut = -1;
        while ((m = re.exec(this.buf))) {
          const end = m.index + m[0].length;
          if (fence !== -1 && end > fence) break;
          if (end >= 40) { cut = end; break; }
        }
        if (cut === -1) {
          if (fence !== -1) {
            const before = this.buf.slice(0, fence);
            if (before.trim()) this.onChunk(before);
            this.onChunk('(код — в чате)');
            this.buf = this.buf.slice(fence + 3);
            this.inCode = true;
            continue;
          }
          return;
        }
        const chunk = this.buf.slice(0, cut);
        this.buf = this.buf.slice(cut);
        if (chunk.trim()) this.onChunk(chunk);
      }
    }
    end() {
      if (!this.inCode && this.buf.trim()) this.onChunk(this.buf);
      this.buf = '';
    }
  }

  // ------------------------------------------------------------------ chat events
  const turns = new Map();

  function onChatEvent(e) {
    st.lastInteraction = Date.now();
    if (st.mode === 'sleeping') st.mode = 'idle';
    if (e.type === 'turn-start') {
      speaker.stop();
      const speak = shouldSpeak(e.source);
      const t = { text: '', tools: [], speak, chunker: null, source: e.source };
      if (speak) t.chunker = new Chunker((c) => speaker.enqueue(c));
      turns.set(e.turnId, t);
      st.mode = 'thinking';
      showBubble({ html: '<span class="dots">хм</span>', sticky: true });
    } else if (e.type === 'emotion') {
      setEmotion(e.emotion, 20000);
      if (e.emotion === 'happy' || e.emotion === 'laugh') hop();
      if (e.emotion === 'angry') shake();
      if (e.emotion === 'shy') fx('♥', 'heart', 8, -2);
      if (e.emotion === 'surprised') fx('!', 'alert', 12, -4);
    } else if (e.type === 'delta') {
      const t = turns.get(e.turnId);
      if (!t) return;
      if (st.mode === 'thinking') st.mode = 'talking';
      if (t.lastStep != null && t.lastStep !== e.step && t.text) t.text = t.text.trimEnd() + '\n';
      t.lastStep = e.step;
      t.text += e.delta;
      if (!t.speak) st.textFlapUntil = Date.now() + 260;
      t.chunker?.push(e.delta);
      renderTurnBubble(t, true);
    } else if (e.type === 'tool') {
      const t = turns.get(e.turnId);
      if (!t) return;
      const existing = t.tools.find((x) => x.id === e.id);
      if (existing) Object.assign(existing, e);
      else t.tools.push({ ...e });
      if (e.status === 'run' && st.mode !== 'talking') st.mode = 'thinking';
      if (e.status === 'done' && e.kind === 'reminder') { fx('⏰', 'alert', 14, -6); hop(); }
      renderTurnBubble(t, true);
    } else if (e.type === 'turn-end') {
      const t = turns.get(e.turnId);
      st.mode = 'idle';
      if (t) {
        t.chunker?.end();
        if (e.text && !t.text) t.text = e.text;
        renderTurnBubble(t, false);
        turns.delete(e.turnId);
      }
      setEmotion(e.emotion || 'neutral', 12000);
    } else if (e.type === 'turn-error') {
      const t = turns.get(e.turnId);
      turns.delete(e.turnId);
      st.mode = 'idle';
      if (e.aborted) {
        if (t?.text) renderTurnBubble(t, false); else hideBubble();
        return;
      }
      setEmotion('sad', 8000);
      Sfx.error();
      showBubble({ head: 'ой…', text: e.message, ms: 12000, opensChat: true });
    }
  }

  function renderTurnBubble(t, streaming) {
    const parts = [];
    for (const tool of t.tools.slice(-3)) {
      const label = tool.status === 'run' ? `⚙️ ${toolHuman(tool.name)}…` : (tool.label || tool.name);
      parts.push(`<span class="tool">${escapeHtml(label)}</span>`);
    }
    const { text, cut } = plainPreview(t.text);
    if (text) parts.push(escapeHtml(text) + (cut ? ' <span class="more">…(в чате)</span>' : ''));
    else if (streaming) parts.push('<span class="dots">хм</span>');
    showBubble({ html: parts.join('<br>'), sticky: streaming, opensChat: cut || !streaming });
    if (!streaming) hideBubbleLater();
  }

  function toolHuman(name) {
    return ({
      create_reminder: 'ставлю напоминание', list_reminders: 'смотрю напоминания', delete_reminder: 'удаляю напоминание',
      update_reminder: 'меняю напоминание', remember: 'запоминаю', forget: 'забываю', get_weather: 'смотрю погоду',
      get_system_info: 'смотрю ПК', open_url: 'открываю ссылку', search_web: 'ищу', open_app: 'запускаю приложение',
      open_folder: 'открываю папку', media_control: 'управляю звуком', system_action: 'выполняю', clipboard_read: 'читаю буфер',
      clipboard_write: 'копирую', take_screenshot: 'смотрю на экран', run_command: 'выполняю команду',
    })[name] || name;
  }

  // ------------------------------------------------------------------ microphone
  const listener = new Listener({
    sensitivity: () => settings?.voice?.sensitivity ?? 0.5,
    silenceMs: () => settings?.voice?.silenceMs ?? 1100,
    deviceId: () => settings?.voice?.micDeviceId || '',
    wakeEnabled: () => !!settings?.voice?.wakeWord,
    onLevel: (v) => {
      if (levelsEl.classList.contains('hidden')) return;
      const bars = levelsEl.children;
      for (let i = 0; i < bars.length; i++) {
        const k = Math.max(0, v * 18 * (0.6 + 0.4 * Math.sin(i * 1.7 + Date.now() / 90)));
        bars[i].style.height = `${3 + Math.min(16, k)}px`;
      }
    },
    onState: (s, reason) => {
      if (s === 'command') {
        st.mode = 'listening';
        if (settings?.voice?.chime) Sfx.listenStart();
        api.pet.line('listening').then((txt) => {
          showBubble({ head: '🎙️ слушаю', text: txt, sticky: true });
          levelsEl.classList.remove('hidden');
        }).catch(() => {});
      } else if (s === 'recording') {
        st.mode = 'recording';
        levelsEl.classList.remove('hidden');
      } else if (s === 'processing') {
        if (settings?.voice?.chime) Sfx.listenStop();
        st.mode = 'processing';
        levelsEl.classList.add('hidden');
        showBubble({ html: '<span class="dots">распознаю</span>', sticky: true });
      } else if ((s === 'off' || s === 'wake') && reason) {
        st.mode = 'idle';
        levelsEl.classList.add('hidden');
        if (reason === 'timeout' || reason === 'short') showBubble({ text: 'Не расслышала~ Ещё раз?', ms: 3500 });
        else hideBubble();
      }
    },
    onSegment: async (wav, { mode }) => {
      if (mode === 'wake') return handleWakeSegment(wav);
      try {
        const text = await api.voice.transcribe(wav, 'audio/wav');
        st.mode = 'idle';
        if (!text || text.replace(/[\s.,!?…]/g, '').length < 2) {
          showBubble({ text: 'Не расслышала~ Ещё раз?', ms: 3500 });
          return;
        }
        api.chat.send({ text, source: 'voice' });
      } catch (e) {
        st.mode = 'idle';
        setEmotion('sad', 6000);
        showBubble({ head: 'микрофон', text: e.message, ms: 9000 });
      }
    },
  });

  let wakeBusy = false;
  async function handleWakeSegment(wav) {
    if (wakeBusy || speaker.active || turns.size) return;
    wakeBusy = true;
    try {
      const text = await api.voice.transcribe(wav, 'audio/wav');
      const norm = (text || '').toLowerCase().replace(/ё/g, 'е');
      const phrases = (settings?.voice?.wakePhrases || 'вера').split(',').map((p) => p.trim().toLowerCase().replace(/ё/g, 'е')).filter(Boolean);
      let rest = null;
      for (const p of phrases) {
        const re = new RegExp(`(^|[^а-яa-z])${p.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}([^а-яa-z]|$)`, 'i');
        const m = norm.match(re);
        if (m) {
          rest = text.slice(m.index + m[0].length).replace(/^[\s,.!?:;—-]+/, '').trim();
          break;
        }
      }
      if (rest == null) return;
      st.lastInteraction = Date.now();
      if (rest.replace(/[\s.,!?…]/g, '').length >= 3) {
        if (settings?.voice?.chime) Sfx.listenStart();
        api.chat.send({ text: rest, source: 'voice' });
      } else {
        startListening();
      }
    } catch (e) {
      console.warn('wake', e);
    } finally {
      wakeBusy = false;
    }
  }

  async function startListening() {
    speaker.stop();
    try {
      await listener.startCommand();
    } catch (e) {
      setEmotion('sad', 6000);
      showBubble({ head: 'микрофон', text: 'Нет доступа к микрофону: ' + e.message, ms: 8000 });
    }
  }

  async function syncWake() {
    const want = !!settings?.voice?.wakeWord;
    try {
      if (want && listener.mode === 'off') await listener.startWake();
      else if (!want && listener.mode === 'wake') listener.shutdown();
    } catch (e) {
      showBubble({ head: 'микрофон', text: 'Голосовая активация не запустилась: ' + e.message, ms: 8000 });
    }
  }

  // ------------------------------------------------------------------ reminders
  function onReminderDue({ reminder, missed, phrase, speak, sound }) {
    st.lastInteraction = Date.now();
    st.mode = 'idle';
    if (sound) Sfx.reminder();
    setEmotion(missed ? 'surprised' : 'happy', 15000);
    fx('!', 'alert', 12, -4);
    hop();
    const close = () => hideBubble();
    showBubble({
      head: missed ? '⏰ пропущенное' : '⏰ напоминание',
      text: phrase,
      sticky: true,
      actions: [
        { label: 'Готово ✓', primary: true, onClick: () => { api.reminders.complete(reminder.id).catch(() => {}); setEmotion('smug', 6000); close(); } },
        { label: '+10 мин', onClick: () => { api.reminders.snooze(reminder.id, 10).catch(() => {}); close(); } },
        { label: '+1 час', onClick: () => { api.reminders.snooze(reminder.id, 60).catch(() => {}); close(); } },
      ],
    });
    setTimeout(() => { if (bubbleSticky && bubbleHead.textContent.startsWith('⏰')) hideBubbleLater(15000); }, 120000);
    if (speak && settings?.tts?.engine !== 'off') {
      speaker.stop();
      setTimeout(() => speaker.enqueue(phrase), sound ? 900 : 0);
    }
  }

  // ------------------------------------------------------------------ mouse
  let ignoring = true;
  let hitBubble = false;
  function updateHitArea() { /* bubble bounds читаем на лету */ }

  function overSprite(clientX, clientY) {
    const r = canvas.getBoundingClientRect();
    if (clientX < r.left || clientX >= r.right || clientY < r.top || clientY >= r.bottom) return false;
    const px = Math.floor(((clientX - r.left) / r.width) * 64);
    const py = Math.floor(((clientY - r.top) / r.height) * 72);
    // небольшой допуск вокруг пикселей
    for (const [dx, dy] of [[0, 0], [1, 0], [-1, 0], [0, 1], [0, -1]]) {
      const x = px + dx; const y = py + dy;
      if (x < 0 || y < 0 || x >= 64 || y >= 72) continue;
      if (g.getImageData(x, y, 1, 1).data[3] > 20) return true;
    }
    return false;
  }

  function overBubble(x, y) {
    if (bubble.classList.contains('hidden')) return false;
    const r = bubble.getBoundingClientRect();
    return x >= r.left - 4 && x <= r.right + 4 && y >= r.top - 4 && y <= r.bottom + 14;
  }

  function setIgnore(v) {
    if (v === ignoring) return;
    ignoring = v;
    api.pet.ignoreMouse(v);
  }

  let drag = null;
  let clickTimer = null;
  let hoverSince = 0;

  window.addEventListener('mousemove', (e) => {
    if (drag) {
      const dx = e.screenX - drag.sx;
      const dy = e.screenY - drag.sy;
      if (!drag.moved && Math.hypot(dx, dy) > 4) {
        drag.moved = true;
        st.dragging = true;
        veraEl.classList.add('dragging');
        hideBubble();
        api.pet.line('drag').then((t) => showBubble({ text: t, ms: 2500 })).catch(() => {});
      }
      if (drag.moved && drag.bounds) {
        const now = performance.now();
        if (now - drag.last > 12) {
          drag.last = now;
          api.pet.move(drag.bounds.x + dx, drag.bounds.y + dy);
        }
      }
      return;
    }
    const onSprite = overSprite(e.clientX, e.clientY);
    hitBubble = overBubble(e.clientX, e.clientY);
    setIgnore(!(onSprite || hitBubble));
    if (onSprite) {
      if (!hoverSince) hoverSince = Date.now();
      else if (Date.now() - hoverSince > 1500 && st.mode === 'idle' && Date.now() > st.emotionUntil && Math.random() < 0.02) {
        setEmotion('smug', 3000);
      }
    } else hoverSince = 0;
  });

  canvas.addEventListener('mousedown', async (e) => {
    if (e.button !== 0) return;
    st.lastInteraction = Date.now();
    if (st.mode === 'sleeping') st.mode = 'idle';
    drag = { sx: e.screenX, sy: e.screenY, moved: false, bounds: null, last: 0 };
    drag.bounds = await api.pet.getBounds().catch(() => null);
  });

  window.addEventListener('mouseup', (e) => {
    if (!drag) return;
    const d = drag;
    drag = null;
    if (d.moved) {
      st.dragging = false;
      veraEl.classList.remove('dragging');
      api.pet.dragEnd();
      setEmotion('angry', 2500);
      return;
    }
    if (e.button !== 0) return;
    if (clickTimer) {
      clearTimeout(clickTimer);
      clickTimer = null;
      poke();
      return;
    }
    clickTimer = setTimeout(() => {
      clickTimer = null;
      api.pet.click();
    }, 260);
  });

  canvas.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    api.pet.menu();
  });

  document.addEventListener('mouseleave', () => {
    if (!drag) setIgnore(true);
  });

  let pokes = 0;
  function poke() {
    pokes++;
    hop();
    const emo = pokes % 4 === 0 ? 'angry' : (Math.random() < 0.5 ? 'laugh' : 'smug');
    setEmotion(emo, 4000);
    if (emo === 'angry') shake();
    api.pet.line('poke').then((t) => showBubble({ text: t, ms: 3500 })).catch(() => {});
    setTimeout(() => { pokes = Math.max(0, pokes - 1); }, 8000);
  }

  // ------------------------------------------------------------------ idle life
  let nextIdleChat = Date.now() + (25 + Math.random() * 20) * 60000;
  setInterval(() => {
    const now = Date.now();
    const h = new Date().getHours();
    const idleFor = now - st.lastInteraction;
    if (st.mode === 'idle' && !speaker.active && (h >= 1 && h < 6) && idleFor > 15 * 60000) {
      st.mode = 'sleeping';
    }
    if (st.mode === 'sleeping') fx('z', 'zzz', 14 + Math.random() * 4, -2);
    if (settings?.pet?.idleChatter && st.mode === 'idle' && now > nextIdleChat && !turns.size && bubble.classList.contains('hidden')) {
      nextIdleChat = now + (25 + Math.random() * 25) * 60000;
      api.pet.line('idle').then((t) => { setEmotion('smug', 6000); showBubble({ text: t }); }).catch(() => {});
    }
  }, 2500);

  // ------------------------------------------------------------------ settings
  function applySettings(s) {
    settings = s;
    document.documentElement.style.setProperty('--scale', String(s.pet?.scale || 3));
    syncWake();
  }

  // ------------------------------------------------------------------ init
  async function init() {
    await loadImages();
    draw(true);
    loop();
    scheduleBlink();
    applySettings(await api.settings.get());
    api.settings.onChange(applySettings);
    api.chat.onEvent(onChatEvent);
    api.reminders.onDue(onReminderDue);
    api.pet.onSay(({ text, emotion, ms }) => {
      setEmotion(emotion || 'neutral', 6000);
      showBubble({ text, ms });
    });
    api.pet.onCommand(({ cmd, payload }) => {
      st.lastInteraction = Date.now();
      if (cmd === 'listen') startListening();
      else if (cmd === 'listen-toggle') {
        if (listener.mode === 'command') listener.finishNow();
        else if (speaker.active) speaker.stop();
        else startListening();
      } else if (cmd === 'stop-speaking') speaker.stop();
      else if (cmd === 'speak' && payload) {
        ttsErrorShown = false;
        speaker.stop();
        setEmotion('smug', 6000);
        showBubble({ text: payload, ms: 6000 });
        speaker.enqueue(payload);
      }
    });
    setTimeout(async () => {
      try {
        const g0 = await api.pet.greeting();
        setEmotion(g0.noKey ? 'angry' : 'smug', 7000);
        hop();
        showBubble({ text: g0.text, ms: 9000, opensChat: g0.noKey });
      } catch (_) { /* ignore */ }
    }, 900);
  }

  init();
})();
