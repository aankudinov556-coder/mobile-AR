'use strict';
const {
  app, BrowserWindow, Tray, Menu, ipcMain, globalShortcut, screen, Notification, shell,
  nativeImage, desktopCapturer, session, powerMonitor, dialog,
} = require('electron');
const path = require('path');

const { Store, SECRET_NAMES } = require('./store');
const { Reminders } = require('./reminders');
const { Memory } = require('./memory');
const { createProviders } = require('./providers');
const { Tools } = require('./tools');
const { Agent } = require('./agent');
const { Voice } = require('./voice');
const { PERSONAS, line, greetingKind } = require('./personas');

const IS_DEV = process.argv.includes('--dev');
const START_HIDDEN = process.argv.includes('--hidden');
const ASSETS = path.join(__dirname, '..', 'renderer', 'assets');
const SPRITE_W = 64;
const SPRITE_H = 72;
const BUBBLE_SPACE = 210;

// для автотестов: отдельная папка данных
if (process.env.VERA_USER_DATA) app.setPath('userData', process.env.VERA_USER_DATA);

if (!app.requestSingleInstanceLock()) {
  app.quit();
  process.exit(0);
}

app.setAppUserModelId('com.vera.assistant');
// прозрачные окна на некоторых видеокартах Windows рисуются чёрными без этого флага
app.commandLine.appendSwitch('disable-features', 'CalculateNativeWinOcclusion');

let store; let reminders; let memory; let providers; let tools; let agent; let voice;
let petWin = null;
let mainWin = null;
let tray = null;
let quitting = false;

// ------------------------------------------------------------------ helpers
function settings() {
  return store.settings;
}

function sendAll(channel, payload) {
  for (const w of [petWin, mainWin]) {
    if (w && !w.isDestroyed()) w.webContents.send(channel, payload);
  }
}

function petSize(scale = settings().pet.scale) {
  const s = Math.max(1, Math.min(8, Math.round(scale)));
  const w = Math.max(340, SPRITE_W * s + 60);
  const h = SPRITE_H * s + BUBBLE_SPACE + 12;
  return { w, h, s };
}

function clampToScreen(x, y, w, h) {
  const display = screen.getDisplayNearestPoint({ x: Math.round(x + w / 2), y: Math.round(y + h / 2) });
  const wa = display.workArea;
  const nx = Math.min(Math.max(x, wa.x - w / 2 + 40), wa.x + wa.width - w / 2 - 40);
  const ny = Math.min(Math.max(y, wa.y - BUBBLE_SPACE), wa.y + wa.height - h);
  return { x: Math.round(nx), y: Math.round(ny) };
}

// ------------------------------------------------------------------ windows
function createPetWindow() {
  const { w, h } = petSize();
  const wa = screen.getPrimaryDisplay().workArea;
  let { x, y } = settings().pet;
  if (x == null || y == null) {
    x = wa.x + wa.width - w - 20;
    y = wa.y + wa.height - h;
  }
  const pos = clampToScreen(x, y, w, h);

  petWin = new BrowserWindow({
    width: w,
    height: h,
    x: pos.x,
    y: pos.y,
    transparent: true,
    frame: false,
    resizable: false,
    movable: true,
    minimizable: false,
    maximizable: false,
    fullscreenable: false,
    skipTaskbar: true,
    hasShadow: false,
    alwaysOnTop: settings().pet.alwaysOnTop,
    show: false,
    backgroundColor: '#00000000',
    title: 'Вера',
    icon: path.join(ASSETS, 'icon.png'),
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      backgroundThrottling: false,
    },
  });
  if (settings().pet.alwaysOnTop) petWin.setAlwaysOnTop(true, 'floating');
  petWin.setIgnoreMouseEvents(true, { forward: true });
  petWin.loadFile(path.join(__dirname, '..', 'renderer', 'pet.html'));
  petWin.once('ready-to-show', () => {
    if (settings().pet.visible) petWin.showInactive();
  });
  petWin.on('closed', () => { petWin = null; });
  guardNavigation(petWin);
  if (IS_DEV) petWin.webContents.openDevTools({ mode: 'detach' });
}

function createMainWindow(show = false) {
  const isWin = process.platform === 'win32';
  mainWin = new BrowserWindow({
    width: 480,
    height: 720,
    minWidth: 380,
    minHeight: 480,
    show: false,
    title: 'Вера',
    backgroundColor: '#17151f',
    icon: path.join(ASSETS, 'icon.png'),
    titleBarStyle: 'hidden',
    titleBarOverlay: isWin ? { color: '#1d1a27', symbolColor: '#b9b3cc', height: 40 } : false,
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, '..', 'preload', 'preload.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
    },
  });
  mainWin.removeMenu();
  mainWin.loadFile(path.join(__dirname, '..', 'renderer', 'app.html'));
  mainWin.on('close', (e) => {
    if (!quitting) {
      e.preventDefault();
      mainWin.hide();
    }
  });
  mainWin.on('closed', () => { mainWin = null; });
  guardNavigation(mainWin);
  if (show) mainWin.once('ready-to-show', () => showMain());
  if (IS_DEV) mainWin.webContents.openDevTools({ mode: 'detach' });
}

function guardNavigation(win) {
  win.webContents.setWindowOpenHandler(({ url }) => {
    if (/^https?:/i.test(url)) shell.openExternal(url);
    return { action: 'deny' };
  });
  win.webContents.on('will-navigate', (e, url) => {
    if (!url.startsWith('file://')) {
      e.preventDefault();
      if (/^https?:/i.test(url)) shell.openExternal(url);
    }
  });
}

function positionMainNearPet() {
  if (!mainWin || !petWin) return;
  const pb = petWin.getBounds();
  const mb = mainWin.getBounds();
  const display = screen.getDisplayMatching(pb);
  const wa = display.workArea;
  let x = pb.x - mb.width + 30;
  if (x < wa.x) x = pb.x + pb.width - 30;
  let y = pb.y + pb.height - mb.height;
  x = Math.min(Math.max(x, wa.x), wa.x + wa.width - mb.width);
  y = Math.min(Math.max(y, wa.y), wa.y + wa.height - mb.height);
  mainWin.setPosition(Math.round(x), Math.round(y));
}

let mainPositioned = false;
function showMain(tab) {
  if (!mainWin) createMainWindow(false);
  if (!mainPositioned) {
    positionMainNearPet();
    mainPositioned = true;
  }
  if (mainWin.isMinimized()) mainWin.restore();
  mainWin.show();
  mainWin.focus();
  if (tab) mainWin.webContents.send('ui:navigate', tab);
}

function toggleMain() {
  if (mainWin && mainWin.isVisible() && mainWin.isFocused()) mainWin.hide();
  else showMain('chat');
}

function setPetVisible(v) {
  store.updateSettings({ pet: { visible: v } });
  if (!petWin) return;
  if (v) petWin.showInactive();
  else petWin.hide();
  rebuildTrayMenu();
}

function applyPetSettings() {
  if (!petWin) return;
  const { w, h } = petSize();
  const b = petWin.getBounds();
  // якорь — нижний центр персонажа
  const cx = b.x + b.width / 2;
  const bottom = b.y + b.height;
  const pos = clampToScreen(cx - w / 2, bottom - h, w, h);
  petWin.setBounds({ x: pos.x, y: pos.y, width: w, height: h });
  petWin.setAlwaysOnTop(!!settings().pet.alwaysOnTop, 'floating');
}

// ------------------------------------------------------------------ tray
function rebuildTrayMenu() {
  if (!tray) return;
  const s = settings();
  tray.setContextMenu(Menu.buildFromTemplate([
    { label: 'Открыть чат', click: () => showMain('chat') },
    { label: 'Сказать Вере (голос)', click: () => petCommand('listen') },
    { label: 'Что у меня на экране?', click: () => askAboutScreen() },
    { type: 'separator' },
    { label: 'Напоминания', click: () => showMain('reminders') },
    { label: 'Память', click: () => showMain('memory') },
    { label: 'Настройки', click: () => showMain('settings') },
    { type: 'separator' },
    { label: 'Показывать Веру на экране', type: 'checkbox', checked: !!s.pet.visible, click: (i) => setPetVisible(i.checked) },
    { label: 'Поверх всех окон', type: 'checkbox', checked: !!s.pet.alwaysOnTop, click: (i) => { store.updateSettings({ pet: { alwaysOnTop: i.checked } }); applyPetSettings(); broadcastSettings(); } },
    { label: 'Голосовая активация «Вера»', type: 'checkbox', checked: !!s.voice.wakeWord, click: (i) => { store.updateSettings({ voice: { wakeWord: i.checked } }); broadcastSettings(); } },
    { type: 'separator' },
    { label: 'Выход', click: () => { quitting = true; app.quit(); } },
  ]));
}

function createTray() {
  const img = nativeImage.createFromPath(path.join(ASSETS, 'tray.png'));
  tray = new Tray(img.isEmpty() ? nativeImage.createEmpty() : img);
  tray.setToolTip('Вера — ИИ ассистент');
  tray.on('click', () => toggleMain());
  tray.on('double-click', () => showMain('chat'));
  rebuildTrayMenu();
}

// ------------------------------------------------------------------ hotkeys
let hotkeyErrors = [];
function registerHotkeys() {
  globalShortcut.unregisterAll();
  hotkeyErrors = [];
  const hk = settings().hotkeys || {};
  const map = {
    toggleChat: () => toggleMain(),
    talk: () => petCommand('listen-toggle'),
    screenshot: () => askAboutScreen(),
  };
  for (const [k, fn] of Object.entries(map)) {
    const acc = (hk[k] || '').trim();
    if (!acc) continue;
    try {
      if (!globalShortcut.register(acc, fn)) hotkeyErrors.push(`${acc} — занято другой программой`);
    } catch (e) {
      hotkeyErrors.push(`${acc} — неверный формат`);
    }
  }
  return hotkeyErrors;
}

function petCommand(cmd, payload) {
  if (petWin && !petWin.isDestroyed()) {
    if (!petWin.isVisible() && settings().pet.visible) petWin.showInactive();
    petWin.webContents.send('pet:command', { cmd, payload });
  }
}

// ------------------------------------------------------------------ screenshot
async function captureScreen() {
  const point = screen.getCursorScreenPoint();
  const display = screen.getDisplayNearestPoint(point);
  const sf = display.scaleFactor || 1;
  const size = { width: Math.round(display.size.width * sf), height: Math.round(display.size.height * sf) };

  // прячем окна Веры на время снимка
  const hidden = [];
  for (const w of [petWin, mainWin]) {
    if (w && !w.isDestroyed() && w.isVisible()) {
      w.setOpacity(0);
      hidden.push(w);
    }
  }
  await new Promise((r) => setTimeout(r, 180));
  let sources;
  try {
    sources = await desktopCapturer.getSources({ types: ['screen'], thumbnailSize: size });
  } finally {
    for (const w of hidden) if (!w.isDestroyed()) w.setOpacity(1);
  }
  if (!sources?.length) throw new Error('Не удалось сделать скриншот');
  const src = sources.find((s) => String(s.display_id) === String(display.id)) || sources[0];
  let img = src.thumbnail;
  const { width, height } = img.getSize();
  const maxEdge = 1600;
  if (Math.max(width, height) > maxEdge) {
    const k = maxEdge / Math.max(width, height);
    img = img.resize({ width: Math.round(width * k), height: Math.round(height * k), quality: 'good' });
  }
  const out = img.getSize();
  return { mime: 'image/jpeg', data: img.toJPEG(82).toString('base64'), width: out.width, height: out.height };
}

async function askAboutScreen() {
  if (!settings().capabilities.screenshots) {
    petSay('Скриншоты выключены в настройках~');
    return;
  }
  try {
    const shot = await captureScreen();
    await agent.send({ text: 'Посмотри на мой экран. Что тут? Если вижу ошибку или проблему — подскажи, что делать.', images: [{ mime: shot.mime, data: shot.data }], source: 'voice' });
  } catch (e) {
    petSay(e.message, 'surprised');
  }
}

function petSay(text, emotion = 'neutral', extra = {}) {
  if (petWin && !petWin.isDestroyed()) petWin.webContents.send('pet:say', { text, emotion, ...extra });
}

// ------------------------------------------------------------------ reminders → user
function onReminderDue(r, { missed }) {
  const s = settings();
  const vars = { name: s.userName || 'семпай', text: r.text };
  const phrase = line(s.persona, missed ? 'missed' : 'reminder', vars);
  if (s.reminders.notification && Notification.isSupported()) {
    const n = new Notification({
      title: missed ? '⏰ Пропущенное напоминание' : '⏰ Вера напоминает',
      body: r.text,
      icon: path.join(ASSETS, 'icon.png'),
      silent: true,
    });
    n.on('click', () => showMain('reminders'));
    n.show();
  }
  sendAll('reminder:due', { reminder: r, missed, phrase, speak: !!s.reminders.speak, sound: !!s.reminders.sound });
  if (petWin && !petWin.isVisible()) petWin.showInactive();
}

// ------------------------------------------------------------------ settings broadcast
function publicSettings() {
  return { ...settings(), _hotkeyErrors: hotkeyErrors, _personas: Object.fromEntries(Object.entries(PERSONAS).map(([k, v]) => [k, v.label])) };
}

function broadcastSettings() {
  rebuildTrayMenu();
  sendAll('settings:changed', publicSettings());
}

function applyAutostart() {
  if (process.platform !== 'win32' && process.platform !== 'darwin') return;
  try {
    app.setLoginItemSettings({ openAtLogin: !!settings().autostart, args: ['--hidden'] });
  } catch (e) {
    console.error('autostart', e);
  }
}

// ------------------------------------------------------------------ IPC
function handle(channel, fn) {
  ipcMain.handle(channel, async (event, ...args) => {
    try {
      return { ok: true, data: await fn(event, ...args) };
    } catch (err) {
      return { ok: false, error: err?.message || String(err) };
    }
  });
}

function setupIpc() {
  handle('app:info', () => ({
    version: app.getVersion(),
    platform: process.platform,
    dataDir: store.dir,
    personas: Object.fromEntries(Object.entries(PERSONAS).map(([k, v]) => [k, { label: v.label, tts: v.tts }])),
  }));
  handle('app:openDataFolder', () => shell.openPath(store.dir));
  handle('app:quit', () => { quitting = true; app.quit(); });
  handle('app:showMain', (e, tab) => showMain(tab));
  handle('app:hideMain', () => mainWin?.hide());
  handle('app:openExternal', (e, url) => {
    if (/^https?:/i.test(url)) return shell.openExternal(url);
    return null;
  });

  // settings
  handle('settings:get', () => publicSettings());
  handle('settings:set', (e, patch) => {
    const prev = JSON.stringify({ hk: settings().hotkeys, pet: settings().pet, auto: settings().autostart });
    const clean = { ...patch };
    delete clean._hotkeyErrors;
    delete clean._personas;
    store.updateSettings(clean);
    const now = JSON.stringify({ hk: settings().hotkeys, pet: settings().pet, auto: settings().autostart });
    if (prev !== now) {
      registerHotkeys();
      applyPetSettings();
      applyAutostart();
      if (petWin) {
        if (settings().pet.visible && !petWin.isVisible()) petWin.showInactive();
        if (!settings().pet.visible && petWin.isVisible()) petWin.hide();
      }
    }
    broadcastSettings();
    return publicSettings();
  });

  // secrets
  handle('secrets:status', () => store.secretStatus());
  handle('secrets:set', (e, name, value) => {
    store.setSecret(name, value);
    sendAll('secrets:changed', store.secretStatus());
    return store.secretStatus();
  });
  handle('secrets:test', async (e, name) => {
    if (!SECRET_NAMES.includes(name)) throw new Error('unknown');
    if (providers[name]) return providers[name].test();
    if (name === 'elevenlabs') {
      const v = await voice.elevenVoices();
      return `Ключ работает (${v.length} голосов)`;
    }
    if (name === 'groq') {
      const key = store.getSecret('groq');
      if (!key) throw new Error('Ключ не задан');
      const res = await fetch('https://api.groq.com/openai/v1/models', { headers: { Authorization: `Bearer ${key}` } });
      if (!res.ok) throw new Error(`Groq: ошибка ${res.status}`);
      return 'Ключ работает';
    }
    throw new Error('unknown');
  });

  // models
  handle('models:list', (e, provider) => {
    const p = providers[provider];
    if (!p) throw new Error('unknown provider');
    return p.listModels();
  });

  // chat
  handle('chat:send', async (e, input) => {
    agent.send(input).catch(() => { /* ошибка уже отправлена событием turn-error */ });
    return true;
  });
  handle('chat:stop', () => agent.stop());
  handle('chat:history', () => agent.visibleHistory());
  handle('chat:clear', () => { agent.clear(); sendAll('chat:cleared'); });
  handle('chat:busy', () => agent.busy);

  // reminders
  handle('reminders:list', () => reminders.list());
  handle('reminders:add', (e, p) => reminders.add({ ...p, source: 'user' }));
  handle('reminders:delete', (e, id) => reminders.remove(id));
  handle('reminders:snooze', (e, id, min) => reminders.snooze(id, min));
  handle('reminders:complete', (e, id) => reminders.complete(id));
  handle('reminders:update', (e, id, patch) => reminders.update(id, patch));

  // memory
  handle('memory:list', () => memory.list());
  handle('memory:add', (e, text) => memory.add(text, 'user'));
  handle('memory:update', (e, id, text) => memory.update(id, text));
  handle('memory:delete', (e, id) => memory.remove(id));
  handle('memory:clear', () => memory.clear());

  // voice
  handle('voice:transcribe', (e, data, mime) => voice.transcribe(Buffer.from(data), mime));
  handle('voice:tts', async (e, text) => {
    const r = await voice.synthesize(text);
    if (r.audio) return { mime: r.mime, audio: new Uint8Array(r.audio) };
    return r;
  });
  handle('voice:elevenVoices', () => voice.elevenVoices());
  handle('screen:capture', () => captureScreen());
  handle('screen:ask', () => askAboutScreen());

  // pet window
  handle('pet:getBounds', () => petWin?.getBounds());
  handle('pet:move', (e, x, y) => {
    if (!petWin) return null;
    const b = petWin.getBounds();
    const pos = clampToScreen(x, y, b.width, b.height);
    petWin.setBounds({ x: pos.x, y: pos.y, width: b.width, height: b.height });
    return pos;
  });
  handle('pet:dragEnd', () => {
    if (!petWin) return;
    const b = petWin.getBounds();
    store.updateSettings({ pet: { x: b.x, y: b.y } });
  });
  ipcMain.on('pet:ignoreMouse', (e, ignore) => {
    if (petWin && !petWin.isDestroyed()) {
      if (ignore) petWin.setIgnoreMouseEvents(true, { forward: true });
      else petWin.setIgnoreMouseEvents(false);
    }
  });
  handle('pet:command', (e, cmd, payload) => petCommand(cmd, payload));
  handle('app:pickFile', async () => {
    const r = await dialog.showOpenDialog(mainWin || undefined, {
      title: 'Выбери программу или ярлык',
      properties: ['openFile'],
      filters: process.platform === 'win32'
        ? [{ name: 'Программы и ярлыки', extensions: ['exe', 'lnk', 'url', 'bat', 'cmd', 'appref-ms'] }, { name: 'Все файлы', extensions: ['*'] }]
        : [{ name: 'Все файлы', extensions: ['*'] }],
    });
    return r.canceled ? null : r.filePaths[0];
  });
  handle('app:hotkeysPause', (e, pause) => {
    if (pause) globalShortcut.unregisterAll();
    else registerHotkeys();
    return hotkeyErrors;
  });
  handle('app:theme', (e, theme) => {
    if (process.platform !== 'win32' || !mainWin) return;
    const dark = theme !== 'light';
    try {
      mainWin.setTitleBarOverlay({ color: dark ? '#1d1a27' : '#ffffff', symbolColor: dark ? '#b9b3cc' : '#4a4358', height: 40 });
    } catch (_) { /* старые версии Windows */ }
    mainWin.setBackgroundColor(dark ? '#15131c' : '#f6f4fa');
  });
  handle('app:winControl', (e, action) => {
    if (!mainWin) return;
    if (action === 'minimize') mainWin.minimize();
    else if (action === 'close') mainWin.hide();
  });
  handle('pet:click', () => {
    if (mainWin && mainWin.isVisible()) mainWin.hide();
    else showMain('chat');
  });
  handle('pet:menu', () => {
    if (!petWin) return;
    Menu.buildFromTemplate([
      { label: 'Открыть чат', click: () => showMain('chat') },
      { label: 'Поговорить голосом', click: () => petCommand('listen') },
      { label: 'Что у меня на экране?', click: () => askAboutScreen() },
      { type: 'separator' },
      { label: 'Напоминания', click: () => showMain('reminders') },
      { label: 'Настройки', click: () => showMain('settings') },
      { type: 'separator' },
      { label: 'Спрятать Веру', click: () => setPetVisible(false) },
      { label: 'Выход', click: () => { quitting = true; app.quit(); } },
    ]).popup({ window: petWin });
  });
  handle('pet:line', (e, kind) => {
    const s = settings();
    return line(s.persona, kind, { name: s.userName || 'семпай' });
  });
  handle('pet:greeting', () => {
    const s = settings();
    const vars = { name: s.userName || 'семпай' };
    const hasKey = !!store.getSecret(s.provider);
    return { text: hasKey ? line(s.persona, greetingKind(), vars) : line(s.persona, 'noKey', vars), noKey: !hasKey };
  });
}

// ------------------------------------------------------------------ lifecycle
app.on('second-instance', () => showMain('chat'));

app.whenReady().then(() => {
  store = new Store();
  reminders = new Reminders(store.collection('reminders', []));
  memory = new Memory(store.collection('memory', []));
  providers = createProviders((name) => store.getSecret(name));
  voice = new Voice(store);
  tools = new Tools({
    store,
    reminders,
    memory,
    captureScreen,
    dialogParent: () => (mainWin && mainWin.isVisible() ? mainWin : null),
  });
  agent = new Agent({ store, providers, tools, memory, historyFile: store.collection('history', []) });

  // разрешаем микрофон и уведомления только нашим окнам
  session.defaultSession.setPermissionRequestHandler((wc, permission, cb) => {
    cb(['media', 'notifications', 'clipboard-read', 'clipboard-sanitized-write', 'speaker-selection'].includes(permission));
  });
  session.defaultSession.setPermissionCheckHandler((wc, permission) =>
    ['media', 'notifications', 'clipboard-read', 'clipboard-sanitized-write', 'speaker-selection'].includes(permission));

  agent.on('event', (e) => sendAll('chat:event', e));
  reminders.on('changed', () => sendAll('reminders:changed'));
  reminders.on('due', onReminderDue);
  memory.on('changed', () => sendAll('memory:changed'));

  setupIpc();
  createPetWindow();
  const firstRun = settings().firstRun;
  createMainWindow(firstRun && !START_HIDDEN);
  if (firstRun) {
    mainWin.webContents.once('did-finish-load', () => mainWin.webContents.send('ui:navigate', 'settings'));
    store.updateSettings({ firstRun: false });
  }
  createTray();
  registerHotkeys();
  applyAutostart();
  // планировщик запускаем, когда окно Веры готово принимать события
  petWin.webContents.once('did-finish-load', () => setTimeout(() => reminders.start(), 1500));

  powerMonitor.on('resume', () => reminders.tick(false));
  screen.on('display-removed', () => applyPetSettings());
  screen.on('display-metrics-changed', () => applyPetSettings());
});

app.on('window-all-closed', (e) => {
  // живём в трее
});

app.on('before-quit', () => {
  quitting = true;
  try { store?.flushAll(); } catch (_) { /* ignore */ }
  try { reminders?.file.flush(); memory?.file.flush(); agent?.historyFile.flush(); } catch (_) { /* ignore */ }
});

app.on('will-quit', () => {
  globalShortcut.unregisterAll();
});

let errorShown = false;
process.on('uncaughtException', (err) => {
  console.error('uncaught', err);
  if (app.isReady() && !quitting && !errorShown) {
    errorShown = true;
    dialog.showErrorBox('Вера: ошибка', String(err?.stack || err));
  }
});
process.on('unhandledRejection', (err) => console.error('unhandled', err));
