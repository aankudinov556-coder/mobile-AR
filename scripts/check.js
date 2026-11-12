#!/usr/bin/env node
/* Быстрые проверки без Electron: синтаксис + логика напоминаний, тегов эмоций, конвертации истории. */
'use strict';
const { execFileSync } = require('child_process');
const fs = require('fs');
const path = require('path');
const assert = require('assert');
const Module = require('module');

const root = path.join(__dirname, '..');
let failed = 0;

function test(name, fn) {
  try {
    fn();
    console.log('  ✓', name);
  } catch (e) {
    failed++;
    console.log('  ✗', name, '\n    ', e.message);
  }
}

console.log('Синтаксис:');
function walk(dir, out = []) {
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, out);
    else if (p.endsWith('.js')) out.push(p);
  }
  return out;
}
for (const f of [...walk(path.join(root, 'src')), ...walk(path.join(root, 'scripts'))]) {
  test(path.relative(root, f), () => execFileSync(process.execPath, ['--check', f], { stdio: 'pipe' }));
}

// заглушка electron для модулей, которые его импортируют
const origLoad = Module._load;
Module._load = function (request, ...rest) {
  if (request === 'electron') {
    return {
      app: { getPath: () => '/tmp', getVersion: () => '0.0.0' },
      safeStorage: { isEncryptionAvailable: () => false },
      shell: {}, clipboard: {}, dialog: {},
    };
  }
  return origLoad.call(this, request, ...rest);
};

console.log('Напоминания:');
const { Reminders, nextDue } = require('../src/main/reminders');
function memFile() {
  return { data: [], save() {}, flush() {} };
}
test('daily переносится на завтра', () => {
  const now = new Date(2026, 10, 12, 10, 0).getTime();
  const due = new Date(2026, 10, 12, 9, 0).getTime();
  const n = new Date(nextDue(due, 'daily', now));
  assert.strictEqual(n.getDate(), 13);
  assert.strictEqual(n.getHours(), 9);
});
test('weekdays пропускает выходные', () => {
  const fri = new Date(2026, 10, 13, 9, 0).getTime(); // пятница
  const n = new Date(nextDue(fri, 'weekdays', fri + 1000));
  assert.strictEqual(n.getDay(), 1);
});
test('monthly', () => {
  const t = new Date(2026, 0, 15, 8, 0).getTime();
  const n = new Date(nextDue(t, 'monthly', t + 1));
  assert.strictEqual(n.getMonth(), 1);
});
test('add + tick срабатывает и помечает done', () => {
  const r = new Reminders(memFile());
  const fired = [];
  r.on('due', (x) => fired.push(x));
  const item = r.add({ text: 'тест', due: Date.now() + 50 });
  assert.strictEqual(r.list().length, 1);
  item.due = Date.now() - 10;
  r.items[0].due = Date.now() - 10;
  r.tick(false);
  assert.strictEqual(fired.length, 1);
  assert.strictEqual(r.items[0].done, true);
});
test('прошедшее время без повтора — ошибка', () => {
  const r = new Reminders(memFile());
  assert.throws(() => r.add({ text: 'x', due: Date.now() - 3600 * 1000 }));
});
test('повторяющееся с прошедшим временем переносится', () => {
  const r = new Reminders(memFile());
  const x = r.add({ text: 'x', due: Date.now() - 3600 * 1000, repeat: 'daily' });
  assert.ok(x.due > Date.now());
});
test('snooze возвращает в активные', () => {
  const r = new Reminders(memFile());
  const x = r.add({ text: 'x', due: Date.now() + 60000 });
  r.complete(x.id);
  assert.strictEqual(r.items[0].done, true);
  r.snooze(x.id, 10);
  assert.strictEqual(r.items[0].done, false);
});

console.log('Парсер времени инструмента:');
const { parseDue } = require('../src/main/tools');
test('in_minutes', () => {
  const t = parseDue({ in_minutes: 20 });
  assert.ok(Math.abs(t - (Date.now() + 20 * 60000)) < 2000);
});
test('datetime локальное', () => {
  const t = parseDue({ datetime: '2030-01-02T09:30' });
  const d = new Date(t);
  assert.strictEqual(d.getHours(), 9);
  assert.strictEqual(d.getMinutes(), 30);
});
test('мусор — ошибка', () => assert.throws(() => parseDue({ datetime: 'завтра' })));

console.log('Теги эмоций:');
const { TagFilter, stripTags } = require('../src/main/agent');
test('тег в начале вырезается при стриминге по кусочкам', () => {
  let emo = null;
  const f = new TagFilter((e) => { emo = e; });
  let out = '';
  for (const piece of ['[sm', 'ug] Хе', '-хе, ', 'семпай~']) out += f.push(piece);
  out += f.flush();
  assert.strictEqual(emo, 'smug');
  assert.strictEqual(out, 'Хе-хе, семпай~');
});
test('текст без тега не трогается', () => {
  const f = new TagFilter(() => {});
  let out = f.push('Привет') + f.push(' мир') + f.flush();
  assert.strictEqual(out, 'Привет мир');
});
test('квадратные скобки, не являющиеся тегом, остаются', () => {
  const f = new TagFilter(() => {});
  const out = f.push('[1] пункт') + f.flush();
  assert.strictEqual(out, '[1] пункт');
});
test('stripTags', () => assert.strictEqual(stripTags('[happy] Ура'), 'Ура'));

console.log('Конвертация истории:');
const { createProviders } = require('../src/main/providers');
const prov = createProviders(() => 'x');
const history = [
  { role: 'user', text: 'Напомни через 5 минут', ctx: 'Сейчас: …' },
  { role: 'assistant', text: 'Ставлю!', toolCalls: [{ id: 'call_1', name: 'create_reminder', args: { text: 'т', in_minutes: 5 } }, { id: 'call_2', name: 'take_screenshot', args: {} }] },
  { role: 'tool', toolCallId: 'call_1', name: 'create_reminder', text: 'ok' },
  { role: 'tool', toolCallId: 'call_2', name: 'take_screenshot', text: 'shot', images: [{ mime: 'image/jpeg', data: 'AAAA' }] },
  { role: 'assistant', text: 'Готово' },
  { role: 'user', text: 'спс' },
];
test('anthropic: tool_result в одном user-сообщении, роли чередуются', () => {
  const n = prov.anthropic.convertHistory(history);
  assert.deepStrictEqual(n.map((m) => m.role), ['user', 'assistant', 'user', 'assistant', 'user']);
  assert.strictEqual(n[2].content.length, 2);
  assert.strictEqual(n[2].content[0].type, 'tool_result');
  assert.strictEqual(n[2].content[1].content[1].type, 'image');
  assert.strictEqual(n[1].content[1].type, 'tool_use');
});
test('openai: tool-сообщения + картинка отдельным user', () => {
  const n = prov.openai.convertHistory(history);
  assert.deepStrictEqual(n.map((m) => m.role), ['user', 'assistant', 'tool', 'tool', 'user', 'assistant', 'user']);
  assert.strictEqual(n[1].tool_calls.length, 2);
  assert.ok(n[4].content[1].image_url.url.startsWith('data:image/jpeg;base64,'));
});

console.log('Markdown (рендерер чата):');
{
  const src = fs.readFileSync(path.join(root, 'src/renderer/markdown.js'), 'utf8');
  const win = {};
  new Function('window', src)(win);
  test('экранирует HTML', () => assert.ok(!win.VeraMarkdown.render('<img src=x onerror=alert(1)>').includes('<img')));
  test('код-блоки', () => assert.ok(win.VeraMarkdown.render('```js\nlet a = 1 < 2;\n```').includes('&lt;')));
  test('незакрытый код при стриминге', () => assert.ok(win.VeraMarkdown.render('пример:\n```py\nprint(1)').includes('<pre>')));
  test('списки и жирный', () => {
    const h = win.VeraMarkdown.render('- **раз**\n- два');
    assert.ok(h.includes('<ul>') && h.includes('<b>раз</b>'));
  });
}

console.log(failed ? `\n✗ Провалено: ${failed}` : '\n✓ Все проверки пройдены');
process.exit(failed ? 1 : 0);
