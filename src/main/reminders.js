'use strict';
/**
 * Напоминания: хранение, повторы, планировщик.
 * Событие 'due' (reminder, { missed }) — пора напомнить.
 * Событие 'changed' — список изменился.
 */
const { EventEmitter } = require('events');
const { uid, formatDateTime, relative } = require('./util');

const REPEATS = ['none', 'hourly', 'daily', 'weekdays', 'weekly', 'monthly'];
const REPEAT_LABELS = {
  none: 'однократно',
  hourly: 'каждый час',
  daily: 'каждый день',
  weekdays: 'по будням',
  weekly: 'каждую неделю',
  monthly: 'каждый месяц',
};
const KEEP_DONE_MS = 7 * 24 * 3600 * 1000;

function stepOnce(ts, repeat) {
  const d = new Date(ts);
  switch (repeat) {
    case 'hourly': d.setHours(d.getHours() + 1); break;
    case 'daily': d.setDate(d.getDate() + 1); break;
    case 'weekly': d.setDate(d.getDate() + 7); break;
    case 'monthly': d.setMonth(d.getMonth() + 1); break;
    case 'weekdays':
      do { d.setDate(d.getDate() + 1); } while (d.getDay() === 0 || d.getDay() === 6);
      break;
    default: return null;
  }
  return d.getTime();
}

/** Следующее срабатывание строго позже now. */
function nextDue(ts, repeat, now = Date.now()) {
  let t = ts;
  let guard = 0;
  while (t <= now && guard++ < 100000) {
    t = stepOnce(t, repeat);
    if (t == null) return null;
  }
  if (repeat === 'weekdays') {
    const d = new Date(t);
    while (d.getDay() === 0 || d.getDay() === 6) d.setDate(d.getDate() + 1);
    t = d.getTime();
  }
  return t;
}

class Reminders extends EventEmitter {
  constructor(file) {
    super();
    this.file = file; // JsonFile с массивом
    if (!Array.isArray(this.file.data)) this.file.data = [];
    this.timer = null;
  }

  get items() {
    return this.file.data;
  }

  start() {
    // всё, что «проспали», пока приложение было закрыто, — сработает с пометкой missed
    this.tick(true);
    this.timer = setInterval(() => this.tick(false), 5000);
  }

  stop() {
    clearInterval(this.timer);
  }

  list() {
    this.cleanup();
    return this.items
      .slice()
      .sort((a, b) => (a.done - b.done) || (a.due - b.due))
      .map((r) => this.describe(r));
  }

  describe(r) {
    return {
      ...r,
      dueText: formatDateTime(r.due),
      relText: relative(r.due),
      repeatText: REPEAT_LABELS[r.repeat] || r.repeat,
    };
  }

  /**
   * @param {{text:string, due:number, repeat?:string, source?:string}} p
   */
  add(p) {
    const text = String(p.text || '').trim();
    if (!text) throw new Error('Пустой текст напоминания');
    const repeat = REPEATS.includes(p.repeat) ? p.repeat : 'none';
    let due = Number(p.due);
    if (!Number.isFinite(due)) throw new Error('Некорректное время напоминания');
    const now = Date.now();
    if (due <= now) {
      if (repeat === 'none') {
        if (now - due > 60 * 1000) throw new Error('Это время уже прошло: ' + formatDateTime(due));
        due = now + 1000;
      } else {
        due = nextDue(due, repeat, now);
      }
    }
    const r = {
      id: uid('r_'),
      text: text.slice(0, 500),
      due,
      repeat,
      source: p.source || 'user',
      created: now,
      done: false,
      firedAt: null,
    };
    this.items.push(r);
    this.file.save();
    this.emit('changed');
    return this.describe(r);
  }

  find(idOrText) {
    if (!idOrText) return null;
    const q = String(idOrText).toLowerCase();
    return this.items.find((r) => r.id === idOrText) ||
      this.items.find((r) => !r.done && r.text.toLowerCase().includes(q)) || null;
  }

  remove(id) {
    const r = this.find(id);
    if (!r) return null;
    this.file.data = this.items.filter((x) => x.id !== r.id);
    this.file.save();
    this.emit('changed');
    return r;
  }

  update(id, patch) {
    const r = this.find(id);
    if (!r) return null;
    if (patch.text != null) r.text = String(patch.text).slice(0, 500);
    if (patch.repeat && REPEATS.includes(patch.repeat)) r.repeat = patch.repeat;
    if (patch.due != null && Number.isFinite(Number(patch.due))) {
      r.due = Number(patch.due);
      r.done = false;
    }
    this.file.save();
    this.emit('changed');
    return this.describe(r);
  }

  snooze(id, minutes = 10) {
    const r = this.find(id);
    if (!r) return null;
    if (r.repeat !== 'none' && !r.done) {
      // для повторяющихся: отдельное разовое напоминание-«отложка»
      return this.add({ text: r.text, due: Date.now() + minutes * 60000, repeat: 'none', source: 'snooze' });
    }
    r.due = Date.now() + minutes * 60000;
    r.done = false;
    r.firedAt = null;
    this.file.save();
    this.emit('changed');
    return this.describe(r);
  }

  complete(id) {
    const r = this.find(id);
    if (!r) return null;
    if (r.repeat === 'none') {
      r.done = true;
      r.firedAt = r.firedAt || Date.now();
    }
    this.file.save();
    this.emit('changed');
    return this.describe(r);
  }

  cleanup() {
    const now = Date.now();
    const before = this.items.length;
    this.file.data = this.items.filter((r) => !(r.done && r.firedAt && now - r.firedAt > KEEP_DONE_MS));
    if (this.file.data.length !== before) this.file.save();
  }

  tick(startup = false) {
    const now = Date.now();
    let changed = false;
    for (const r of this.items) {
      if (r.done || r.due > now) continue;
      const missed = now - r.due > 2 * 60 * 1000;
      const snapshot = this.describe(r);
      if (r.repeat === 'none') {
        r.done = true;
        r.firedAt = now;
      } else {
        r.firedAt = now;
        r.due = nextDue(r.due, r.repeat, now);
      }
      changed = true;
      this.emit('due', snapshot, { missed: missed || startup });
    }
    if (changed) {
      this.file.save();
      this.emit('changed');
    }
  }
}

module.exports = { Reminders, REPEATS, REPEAT_LABELS, nextDue };
