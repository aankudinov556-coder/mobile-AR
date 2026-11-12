'use strict';
const { contextBridge, ipcRenderer } = require('electron');

async function call(channel, ...args) {
  const res = await ipcRenderer.invoke(channel, ...args);
  if (!res || !res.ok) throw new Error(res?.error || 'Ошибка');
  return res.data;
}

function on(channel, cb) {
  const fn = (_e, payload) => cb(payload);
  ipcRenderer.on(channel, fn);
  return () => ipcRenderer.removeListener(channel, fn);
}

contextBridge.exposeInMainWorld('vera', {
  app: {
    info: () => call('app:info'),
    openDataFolder: () => call('app:openDataFolder'),
    quit: () => call('app:quit'),
    showMain: (tab) => call('app:showMain', tab),
    hideMain: () => call('app:hideMain'),
    openExternal: (url) => call('app:openExternal', url),
    pickFile: () => call('app:pickFile'),
    hotkeysPause: (pause) => call('app:hotkeysPause', pause),
    winControl: (action) => call('app:winControl', action),
    setTheme: (theme) => call('app:theme', theme),
  },
  settings: {
    get: () => call('settings:get'),
    set: (patch) => call('settings:set', patch),
    onChange: (cb) => on('settings:changed', cb),
  },
  secrets: {
    status: () => call('secrets:status'),
    set: (name, value) => call('secrets:set', name, value),
    test: (name) => call('secrets:test', name),
    onChange: (cb) => on('secrets:changed', cb),
  },
  models: {
    list: (provider) => call('models:list', provider),
  },
  chat: {
    send: (input) => call('chat:send', input),
    stop: () => call('chat:stop'),
    history: () => call('chat:history'),
    clear: () => call('chat:clear'),
    busy: () => call('chat:busy'),
    onEvent: (cb) => on('chat:event', cb),
    onCleared: (cb) => on('chat:cleared', cb),
  },
  reminders: {
    list: () => call('reminders:list'),
    add: (p) => call('reminders:add', p),
    remove: (id) => call('reminders:delete', id),
    snooze: (id, min) => call('reminders:snooze', id, min),
    complete: (id) => call('reminders:complete', id),
    update: (id, patch) => call('reminders:update', id, patch),
    onChange: (cb) => on('reminders:changed', cb),
    onDue: (cb) => on('reminder:due', cb),
  },
  memory: {
    list: () => call('memory:list'),
    add: (text) => call('memory:add', text),
    update: (id, text) => call('memory:update', id, text),
    remove: (id) => call('memory:delete', id),
    clear: () => call('memory:clear'),
    onChange: (cb) => on('memory:changed', cb),
  },
  voice: {
    transcribe: (data, mime) => call('voice:transcribe', data, mime),
    tts: (text) => call('voice:tts', text),
    elevenVoices: () => call('voice:elevenVoices'),
  },
  screen: {
    capture: () => call('screen:capture'),
    ask: () => call('screen:ask'),
  },
  pet: {
    getBounds: () => call('pet:getBounds'),
    move: (x, y) => call('pet:move', x, y),
    dragEnd: () => call('pet:dragEnd'),
    ignoreMouse: (ignore) => ipcRenderer.send('pet:ignoreMouse', !!ignore),
    click: () => call('pet:click'),
    menu: () => call('pet:menu'),
    command: (cmd, payload) => call('pet:command', cmd, payload),
    line: (kind) => call('pet:line', kind),
    greeting: () => call('pet:greeting'),
    onCommand: (cb) => on('pet:command', cb),
    onSay: (cb) => on('pet:say', cb),
  },
  ui: {
    onNavigate: (cb) => on('ui:navigate', cb),
  },
});
