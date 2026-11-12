'use strict';
/** Долгая память: факты о пользователе, которые Вера помнит между сессиями. */
const { EventEmitter } = require('events');
const { uid } = require('./util');

const MAX_FACTS = 150;

class Memory extends EventEmitter {
  constructor(file) {
    super();
    this.file = file;
    if (!Array.isArray(this.file.data)) this.file.data = [];
  }

  get items() {
    return this.file.data;
  }

  list() {
    return this.items.slice();
  }

  add(text, source = 'user') {
    const t = String(text || '').trim().slice(0, 400);
    if (!t) throw new Error('Пустой факт');
    const dup = this.items.find((m) => m.text.toLowerCase() === t.toLowerCase());
    if (dup) return dup;
    const m = { id: uid('m_'), text: t, source, created: Date.now() };
    this.items.push(m);
    while (this.items.length > MAX_FACTS) this.items.shift();
    this.file.save();
    this.emit('changed');
    return m;
  }

  update(id, text) {
    const m = this.items.find((x) => x.id === id);
    if (!m) return null;
    m.text = String(text || '').trim().slice(0, 400) || m.text;
    this.file.save();
    this.emit('changed');
    return m;
  }

  remove(idOrText) {
    const q = String(idOrText || '').toLowerCase();
    const m = this.items.find((x) => x.id === idOrText) ||
      this.items.find((x) => q && x.text.toLowerCase().includes(q));
    if (!m) return null;
    this.file.data = this.items.filter((x) => x.id !== m.id);
    this.file.save();
    this.emit('changed');
    return m;
  }

  clear() {
    this.file.data = [];
    this.file.save();
    this.emit('changed');
  }

  forPrompt() {
    if (!this.items.length) return '(пока ничего)';
    return this.items.map((m) => `- [${m.id}] ${m.text}`).join('\n');
  }
}

module.exports = { Memory };
