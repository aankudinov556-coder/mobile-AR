'use strict';
const crypto = require('crypto');

const WEEKDAYS = ['воскресенье', 'понедельник', 'вторник', 'среда', 'четверг', 'пятница', 'суббота'];
const MONTHS = ['января', 'февраля', 'марта', 'апреля', 'мая', 'июня', 'июля', 'августа',
  'сентября', 'октября', 'ноября', 'декабря'];

function uid(prefix = '') {
  return prefix + crypto.randomBytes(6).toString('hex');
}

function pad(n) {
  return String(n).padStart(2, '0');
}

/** «12 ноября 2026, 15:04» */
function formatDateTime(ts) {
  const d = new Date(ts);
  return `${d.getDate()} ${MONTHS[d.getMonth()]} ${d.getFullYear()}, ${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

/** Локальное время в ISO без смещения: 2026-11-12T15:04 */
function localIso(ts) {
  const d = new Date(ts);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}

function plural(n, one, few, many) {
  const a = Math.abs(n) % 100;
  const b = a % 10;
  if (a > 10 && a < 20) return many;
  if (b > 1 && b < 5) return few;
  if (b === 1) return one;
  return many;
}

/** «через 2 ч 5 мин» / «5 мин назад» */
function relative(ts, now = Date.now()) {
  let diff = Math.round((ts - now) / 60000);
  const past = diff < 0;
  diff = Math.abs(diff);
  let s;
  if (diff < 1) s = 'меньше минуты';
  else if (diff < 60) s = `${diff} мин`;
  else if (diff < 60 * 24) {
    const h = Math.floor(diff / 60);
    const m = diff % 60;
    s = `${h} ч${m ? ' ' + m + ' мин' : ''}`;
  } else {
    const days = Math.floor(diff / 1440);
    const h = Math.floor((diff % 1440) / 60);
    s = `${days} ${plural(days, 'день', 'дня', 'дней')}${h ? ' ' + h + ' ч' : ''}`;
  }
  return past ? `${s} назад` : `через ${s}`;
}

function timeContext(now = Date.now()) {
  const d = new Date(now);
  const tz = Intl.DateTimeFormat().resolvedOptions().timeZone || '';
  const off = -d.getTimezoneOffset();
  const sign = off >= 0 ? '+' : '-';
  const offStr = `UTC${sign}${pad(Math.floor(Math.abs(off) / 60))}:${pad(Math.abs(off) % 60)}`;
  return `Сейчас: ${WEEKDAYS[d.getDay()]}, ${formatDateTime(now)} (локальное время ${localIso(now)}, ${tz} ${offStr})`;
}

function truncate(s, n) {
  s = String(s ?? '');
  return s.length > n ? s.slice(0, n) + `… [обрезано, всего ${s.length} симв.]` : s;
}

module.exports = { uid, formatDateTime, localIso, relative, timeContext, truncate, plural, WEEKDAYS, pad };
