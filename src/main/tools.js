'use strict';
/**
 * Инструменты (function calling), которыми пользуется Вера.
 * definitions(settings) -> [{name, description, parameters}]
 * execute(name, args) -> { text, images?, isError?, label }
 */
const { shell, clipboard, dialog, app } = require('electron');
const { execFile, spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');
const { formatDateTime, truncate, localIso } = require('./util');
const { REPEATS } = require('./reminders');

const IS_WIN = process.platform === 'win32';

function run(cmd, args, { timeout = 15000, windowsVerbatimArguments = false } = {}) {
  return new Promise((resolve) => {
    execFile(cmd, args, { windowsHide: true, timeout, maxBuffer: 4 * 1024 * 1024, encoding: 'utf8', windowsVerbatimArguments }, (err, stdout, stderr) => {
      resolve({ code: err ? (err.code ?? 1) : 0, stdout: String(stdout || ''), stderr: String(stderr || ''), error: err });
    });
  });
}

function powershell(script, opts) {
  // -EncodedCommand (UTF-16LE base64) — без проблем с кавычками и спецсимволами
  const full = '[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; $ProgressPreference="SilentlyContinue"; ' + script;
  const encoded = Buffer.from(full, 'utf16le').toString('base64');
  return run('powershell.exe', ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded], opts);
}

function launchDetached(cmd, args = []) {
  return new Promise((resolve, reject) => {
    try {
      const child = spawn(cmd, args, { detached: true, stdio: 'ignore', windowsHide: false });
      child.once('error', reject);
      child.once('spawn', () => { child.unref(); resolve(); });
    } catch (e) {
      reject(e);
    }
  });
}

/** Windows: `start "" "<target>"` — запуск по имени/пути через оболочку. */
function shellStart(target) {
  return run('cmd.exe', ['/d', '/s', '/c', `start "" "${target}"`], { timeout: 8000, windowsVerbatimArguments: true });
}

function sendKeys(code, times = 1) {
  return powershell(`$w=New-Object -ComObject WScript.Shell; for($i=0;$i -lt ${times};$i++){ $w.SendKeys([char]${code}) }`);
}

function normalize(s) {
  return String(s || '').toLowerCase().replace(/ё/g, 'е').replace(/[^a-zа-я0-9+#]+/g, ' ').trim();
}

const APP_ALIASES = {
  'блокнот': 'notepad', 'калькулятор': 'calc', 'проводник': 'explorer', 'пейнт': 'mspaint', 'paint': 'mspaint',
  'диспетчер задач': 'taskmgr', 'task manager': 'taskmgr', 'командная строка': 'cmd', 'терминал': 'wt',
  'terminal': 'wt', 'параметры': 'ms-settings:', 'настройки windows': 'ms-settings:', 'settings': 'ms-settings:',
  'панель управления': 'control', 'control panel': 'control',
  'телеграм': 'telegram', 'телеграмм': 'telegram', 'дискорд': 'discord', 'стим': 'steam', 'спотифай': 'spotify',
  'хром': 'chrome', 'гугл хром': 'chrome', 'файрфокс': 'firefox', 'фаерфокс': 'firefox', 'эдж': 'edge',
  'яндекс браузер': 'yandex', 'яндекс': 'yandex', 'ворд': 'word', 'эксель': 'excel', 'повер поинт': 'powerpoint',
  'вс код': 'visual studio code', 'vscode': 'visual studio code', 'vs code': 'visual studio code', 'обс': 'obs',
  'вотсап': 'whatsapp', 'ватсап': 'whatsapp', 'зум': 'zoom', 'скайп': 'skype', 'опера': 'opera',
};
const BUILTIN_EXE = {
  notepad: 'notepad.exe', calc: 'calc.exe', explorer: 'explorer.exe', mspaint: 'mspaint.exe', taskmgr: 'taskmgr.exe',
  cmd: 'cmd.exe', powershell: 'powershell.exe', wt: 'wt.exe', control: 'control.exe', 'ms-settings:': 'ms-settings:',
};

let shortcutCache = null;
let shortcutCacheTime = 0;

function walk(dir, depth, out) {
  if (depth < 0) return;
  let entries;
  try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch (_) { return; }
  for (const e of entries) {
    const p = path.join(dir, e.name);
    if (e.isDirectory()) walk(p, depth - 1, out);
    else if (/\.(lnk|url|exe|appref-ms)$/i.test(e.name) && !/uninstall|удален|деинстал/i.test(e.name)) {
      out.push({ name: e.name.replace(/\.(lnk|url|exe|appref-ms)$/i, ''), path: p });
    }
  }
}

function startMenuShortcuts() {
  if (shortcutCache && Date.now() - shortcutCacheTime < 10 * 60 * 1000) return shortcutCache;
  const out = [];
  if (IS_WIN) {
    const dirs = [
      path.join(process.env.ProgramData || 'C:\\ProgramData', 'Microsoft', 'Windows', 'Start Menu', 'Programs'),
      path.join(process.env.APPDATA || '', 'Microsoft', 'Windows', 'Start Menu', 'Programs'),
      app.getPath('desktop'),
      path.join(process.env.PUBLIC || 'C:\\Users\\Public', 'Desktop'),
    ];
    for (const d of dirs) walk(d, 4, out);
  }
  shortcutCache = out;
  shortcutCacheTime = Date.now();
  return out;
}

function score(query, name) {
  const q = normalize(query);
  const n = normalize(name);
  if (!q || !n) return 0;
  if (n === q) return 100;
  if (n.startsWith(q)) return 80;
  if (n.includes(q)) return 60;
  const qw = q.split(' ');
  const hits = qw.filter((w) => w.length > 1 && n.includes(w)).length;
  return hits ? 20 + 30 * (hits / qw.length) : 0;
}

const WMO = {
  0: 'ясно', 1: 'преимущественно ясно', 2: 'переменная облачность', 3: 'пасмурно', 45: 'туман', 48: 'изморозь',
  51: 'лёгкая морось', 53: 'морось', 55: 'сильная морось', 56: 'ледяная морось', 57: 'ледяная морось',
  61: 'небольшой дождь', 63: 'дождь', 65: 'сильный дождь', 66: 'ледяной дождь', 67: 'ледяной дождь',
  71: 'небольшой снег', 73: 'снег', 75: 'сильный снег', 77: 'снежная крупа', 80: 'ливень', 81: 'ливни',
  82: 'сильный ливень', 85: 'снегопад', 86: 'сильный снегопад', 95: 'гроза', 96: 'гроза с градом', 99: 'гроза с градом',
};

function parseDue(args) {
  const now = Date.now();
  if (args.in_minutes != null && args.in_minutes !== '') {
    const m = Number(args.in_minutes);
    if (!Number.isFinite(m) || m <= 0) throw new Error('in_minutes должно быть положительным числом');
    return now + Math.round(m * 60000);
  }
  if (args.datetime) {
    const s = String(args.datetime).trim().replace(' ', 'T');
    const t = Date.parse(s);
    if (Number.isNaN(t)) throw new Error(`Не понимаю дату «${args.datetime}». Используй формат YYYY-MM-DDTHH:MM (локальное время).`);
    return t;
  }
  throw new Error('Нужно указать datetime или in_minutes');
}

class Tools {
  /**
   * @param {object} deps { store, reminders, memory, captureScreen: async()=>{mime,data,width,height} }
   */
  constructor(deps) {
    this.d = deps;
  }

  definitions(settings) {
    const caps = settings.capabilities || {};
    const defs = [
      {
        name: 'create_reminder',
        description:
          'Create a reminder / timer / alarm. Vera will notify the user at that time (Windows notification, voice, speech bubble). ' +
          'Use it whenever the user asks to remind, set a timer or an alarm. Compute times from the current local time given in <context>. ' +
          'Use in_minutes for relative times ("через 20 минут"), datetime for absolute ones ("завтра в 9").',
        parameters: {
          type: 'object',
          properties: {
            text: { type: 'string', description: 'What to remind about, short, in the user\'s language (e.g. "Выпить таблетки").' },
            datetime: { type: 'string', description: 'Local date-time YYYY-MM-DDTHH:MM (no timezone) for the first trigger.' },
            in_minutes: { type: 'number', description: 'Alternatively: minutes from now (can be fractional).' },
            repeat: { type: 'string', enum: REPEATS, description: 'Repeat rule. Default "none".' },
          },
          required: ['text'],
        },
      },
      {
        name: 'list_reminders',
        description: 'List the user\'s active (and recently fired) reminders with their ids.',
        parameters: { type: 'object', properties: {}, required: [] },
      },
      {
        name: 'delete_reminder',
        description: 'Delete / cancel a reminder by id (from list_reminders) or by a fragment of its text.',
        parameters: {
          type: 'object',
          properties: { id: { type: 'string', description: 'Reminder id like r_abc123 or a text fragment.' } },
          required: ['id'],
        },
      },
      {
        name: 'update_reminder',
        description: 'Change text, time or repeat rule of an existing reminder.',
        parameters: {
          type: 'object',
          properties: {
            id: { type: 'string' },
            text: { type: 'string' },
            datetime: { type: 'string', description: 'New local date-time YYYY-MM-DDTHH:MM' },
            in_minutes: { type: 'number' },
            repeat: { type: 'string', enum: REPEATS },
          },
          required: ['id'],
        },
      },
      {
        name: 'remember',
        description:
          'Save a durable fact about the user to long-term memory (name, birthday, preferences, ongoing projects, important people). ' +
          'Do not store trivia or one-off requests. Write the fact as a short standalone sentence in Russian.',
        parameters: { type: 'object', properties: { fact: { type: 'string' } }, required: ['fact'] },
      },
      {
        name: 'forget',
        description: 'Remove a fact from long-term memory by id (like m_abc123) or by text fragment.',
        parameters: { type: 'object', properties: { id: { type: 'string' } }, required: ['id'] },
      },
      {
        name: 'get_weather',
        description: 'Current weather and a 3-day forecast for a city.',
        parameters: { type: 'object', properties: { city: { type: 'string', description: 'City name, e.g. "Москва"' } }, required: ['city'] },
      },
      {
        name: 'get_system_info',
        description: 'Get info about the user\'s PC: OS, CPU load, RAM usage, uptime, battery.',
        parameters: { type: 'object', properties: {}, required: [] },
      },
    ];

    if (caps.pcControl) {
      defs.push(
        {
          name: 'open_url',
          description: 'Open a URL in the default browser (http/https), or a special link (mailto:, tg:, steam:, ms-settings:).',
          parameters: { type: 'object', properties: { url: { type: 'string' } }, required: ['url'] },
        },
        {
          name: 'search_web',
          description: 'Open a search results page in the user\'s browser (for the user to look at).',
          parameters: {
            type: 'object',
            properties: {
              query: { type: 'string' },
              engine: { type: 'string', enum: ['google', 'yandex', 'youtube', 'wikipedia', 'bing', 'duckduckgo'] },
            },
            required: ['query'],
          },
        },
        {
          name: 'open_app',
          description:
            'Launch an application on the user\'s PC by name. Prefer the name as shown in the Windows Start menu, e.g. "Telegram", ' +
            '"Google Chrome", "Steam", "Visual Studio Code", "Блокнот", "Калькулятор".',
          parameters: { type: 'object', properties: { name: { type: 'string' } }, required: ['name'] },
        },
        {
          name: 'open_folder',
          description:
            'Open a folder or file in Explorer. Accepts an absolute path or an alias: desktop, downloads, documents, pictures, music, videos, home.',
          parameters: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'] },
        },
        {
          name: 'media_control',
          description: 'Control media playback and system volume.',
          parameters: {
            type: 'object',
            properties: {
              action: { type: 'string', enum: ['play_pause', 'next', 'previous', 'volume_up', 'volume_down', 'mute', 'set_volume'] },
              level: { type: 'number', description: 'Volume 0-100 for set_volume; step count for volume_up/down (default 5 steps = 10%).' },
            },
            required: ['action'],
          },
        },
        {
          name: 'system_action',
          description: 'System actions: lock the PC, minimize all windows (show desktop), open task manager, open Windows settings.',
          parameters: {
            type: 'object',
            properties: { action: { type: 'string', enum: ['lock', 'show_desktop', 'task_manager', 'settings'] } },
            required: ['action'],
          },
        },
      );
    }
    if (caps.clipboard) {
      defs.push(
        {
          name: 'clipboard_read',
          description: 'Read the text currently in the user\'s clipboard.',
          parameters: { type: 'object', properties: {}, required: [] },
        },
        {
          name: 'clipboard_write',
          description: 'Put text into the user\'s clipboard (e.g. a translated text or code the user asked for).',
          parameters: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] },
        },
      );
    }
    if (caps.screenshots) {
      defs.push({
        name: 'take_screenshot',
        description:
          'Take a screenshot of the user\'s screen to see what they are looking at. Use when the user asks about something on the screen ' +
          '("что у меня на экране", "посмотри", "помоги с этой ошибкой").',
        parameters: { type: 'object', properties: {}, required: [] },
      });
    }
    if (caps.runCommands) {
      defs.push({
        name: 'run_command',
        description:
          (IS_WIN ? 'Run a PowerShell command' : 'Run a shell command') +
          ' on the user\'s PC and return its output. The user must confirm every command. Use only when really needed; never destructive commands.',
        parameters: {
          type: 'object',
          properties: {
            command: { type: 'string' },
            reason: { type: 'string', description: 'Short explanation in Russian shown to the user in the confirmation dialog.' },
          },
          required: ['command'],
        },
      });
    }
    return defs;
  }

  async execute(name, args = {}) {
    try {
      if (args.__invalid_json) return { text: 'Ошибка: аргументы инструмента не являются корректным JSON', isError: true, label: name };
      const fn = this['t_' + name];
      if (!fn) return { text: `Неизвестный инструмент: ${name}`, isError: true, label: name };
      const r = await fn.call(this, args || {});
      return typeof r === 'string' ? { text: r, label: name } : r;
    } catch (err) {
      return { text: 'Ошибка: ' + (err?.message || String(err)), isError: true, label: `⚠️ ${name}` };
    }
  }

  // ------------------------------------------------------------ reminders
  t_create_reminder(a) {
    const due = parseDue(a);
    const r = this.d.reminders.add({ text: a.text, due, repeat: a.repeat || 'none', source: 'ai' });
    return {
      text: `Напоминание создано (id ${r.id}): «${r.text}» — ${r.dueText} (${r.relText}), повтор: ${r.repeatText}.`,
      label: `⏰ ${r.text} — ${r.dueText}${r.repeat !== 'none' ? ' · ' + r.repeatText : ''}`,
      kind: 'reminder',
    };
  }

  t_list_reminders() {
    const list = this.d.reminders.list();
    if (!list.length) return { text: 'Напоминаний нет.', label: '⏰ список напоминаний' };
    const active = list.filter((r) => !r.done);
    const done = list.filter((r) => r.done).slice(-5);
    const fmt = (r) => `- [${r.id}] «${r.text}» — ${r.dueText} (${r.relText}), ${r.repeatText}`;
    return {
      text: `Активные (${active.length}):\n${active.map(fmt).join('\n') || '—'}\n\nНедавно сработавшие:\n${done.map(fmt).join('\n') || '—'}`,
      label: `⏰ напоминаний: ${active.length}`,
    };
  }

  t_delete_reminder(a) {
    const r = this.d.reminders.remove(a.id);
    if (!r) return { text: `Напоминание «${a.id}» не найдено. Вызови list_reminders, чтобы узнать id.`, isError: true, label: '⏰ не найдено' };
    return { text: `Удалено напоминание «${r.text}».`, label: `🗑️ ${r.text}` };
  }

  t_update_reminder(a) {
    const patch = { text: a.text, repeat: a.repeat };
    if (a.datetime || a.in_minutes != null) patch.due = parseDue(a);
    const r = this.d.reminders.update(a.id, patch);
    if (!r) return { text: `Напоминание «${a.id}» не найдено.`, isError: true, label: '⏰ не найдено' };
    return { text: `Обновлено: «${r.text}» — ${r.dueText} (${r.relText}), ${r.repeatText}.`, label: `⏰ ${r.text} — ${r.dueText}` };
  }

  // ------------------------------------------------------------ memory
  t_remember(a) {
    const m = this.d.memory.add(a.fact, 'ai');
    return { text: `Запомнила (id ${m.id}): ${m.text}`, label: `🧠 ${m.text}` };
  }

  t_forget(a) {
    const m = this.d.memory.remove(a.id);
    if (!m) return { text: 'Такого факта нет в памяти.', isError: true, label: '🧠 не найдено' };
    return { text: `Забыла: ${m.text}`, label: `🧠 забыто: ${m.text}` };
  }

  // ------------------------------------------------------------ info
  async t_get_weather(a) {
    const city = String(a.city || '').trim();
    if (!city) throw new Error('Не указан город');
    const geo = await fetch(`https://geocoding-api.open-meteo.com/v1/search?count=1&language=ru&name=${encodeURIComponent(city)}`).then((r) => r.json());
    const place = geo?.results?.[0];
    if (!place) return { text: `Город «${city}» не найден.`, isError: true, label: '🌦️ город не найден' };
    const url = `https://api.open-meteo.com/v1/forecast?latitude=${place.latitude}&longitude=${place.longitude}` +
      '&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m' +
      '&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max&timezone=auto&forecast_days=3&wind_speed_unit=ms';
    const w = await fetch(url).then((r) => r.json());
    const c = w.current;
    const lines = [
      `${place.name}${place.country ? ', ' + place.country : ''}: сейчас ${Math.round(c.temperature_2m)}°C (ощущается ${Math.round(c.apparent_temperature)}°C), ` +
      `${WMO[c.weather_code] || 'код ' + c.weather_code}, ветер ${Math.round(c.wind_speed_10m)} м/с, влажность ${c.relative_humidity_2m}%.`,
    ];
    const days = ['Сегодня', 'Завтра', 'Послезавтра'];
    (w.daily?.time || []).forEach((t, i) => {
      lines.push(`${days[i] || t}: ${Math.round(w.daily.temperature_2m_min[i])}…${Math.round(w.daily.temperature_2m_max[i])}°C, ` +
        `${WMO[w.daily.weather_code[i]] || ''}, осадки ${w.daily.precipitation_probability_max[i] ?? '?'}%`);
    });
    return { text: lines.join('\n'), label: `🌦️ ${place.name}: ${Math.round(c.temperature_2m)}°C` };
  }

  async t_get_system_info() {
    const sample = () => os.cpus().map((c) => c.times);
    const a = sample();
    await new Promise((r) => setTimeout(r, 400));
    const b = sample();
    let idle = 0; let total = 0;
    b.forEach((t, i) => {
      const p = a[i];
      const tot = Object.keys(t).reduce((s, k) => s + (t[k] - p[k]), 0);
      idle += t.idle - p.idle;
      total += tot;
    });
    const cpu = total ? Math.round(100 * (1 - idle / total)) : 0;
    const totalMem = os.totalmem();
    const used = totalMem - os.freemem();
    const gb = (x) => (x / 1024 ** 3).toFixed(1);
    const up = os.uptime();
    const lines = [
      `ОС: ${os.type()} ${os.release()} (${os.arch()}), имя ПК: ${os.hostname()}`,
      `CPU: ${os.cpus()[0]?.model?.trim() || '?'} ×${os.cpus().length}, загрузка ${cpu}%`,
      `ОЗУ: занято ${gb(used)} из ${gb(totalMem)} ГБ (${Math.round((used / totalMem) * 100)}%)`,
      `Аптайм: ${Math.floor(up / 3600)} ч ${Math.floor((up % 3600) / 60)} мин`,
      `Время: ${formatDateTime(Date.now())}`,
    ];
    if (IS_WIN) {
      const r = await powershell('$b=Get-CimInstance Win32_Battery | Select-Object -First 1 EstimatedChargeRemaining,BatteryStatus; if($b){ "$($b.EstimatedChargeRemaining)|$($b.BatteryStatus)" }', { timeout: 8000 });
      const m = r.stdout.trim().match(/^(\d+)\|(\d+)/);
      if (m) lines.push(`Батарея: ${m[1]}%${m[2] === '2' ? ', заряжается' : ''}`);
    }
    return { text: lines.join('\n'), label: `🖥️ CPU ${cpu}% · RAM ${Math.round((used / totalMem) * 100)}%` };
  }

  // ------------------------------------------------------------ pc control
  async t_open_url(a) {
    const url = String(a.url || '').trim();
    if (!/^(https?:|mailto:|tg:|steam:|ms-settings:|spotify:|discord:)/i.test(url)) {
      throw new Error('Разрешены только ссылки http(s), mailto:, tg:, steam:, ms-settings:, spotify:, discord:');
    }
    await shell.openExternal(url);
    return { text: `Открыто: ${url}`, label: `🌐 ${url.replace(/^https?:\/\//, '').slice(0, 60)}` };
  }

  async t_search_web(a) {
    const q = encodeURIComponent(String(a.query || ''));
    const engines = {
      google: `https://www.google.com/search?q=${q}`,
      yandex: `https://yandex.ru/search/?text=${q}`,
      youtube: `https://www.youtube.com/results?search_query=${q}`,
      wikipedia: `https://ru.wikipedia.org/w/index.php?search=${q}`,
      bing: `https://www.bing.com/search?q=${q}`,
      duckduckgo: `https://duckduckgo.com/?q=${q}`,
    };
    const url = engines[a.engine] || engines.google;
    await shell.openExternal(url);
    return { text: `Открыла поиск «${a.query}» (${a.engine || 'google'}). Результатов я не вижу — только открыла страницу.`, label: `🔎 ${a.query}` };
  }

  async t_open_app(a) {
    const raw = String(a.name || '').trim();
    if (!raw) throw new Error('Не указано имя приложения');
    const settings = this.d.store.settings;
    const n = normalize(raw);
    const alias = APP_ALIASES[n] || n;

    // 1) пользовательские ярлыки из настроек
    const custom = (settings.appShortcuts || [])
      .map((s) => ({ ...s, score: Math.max(score(raw, s.name), score(alias, s.name)) }))
      .sort((x, y) => y.score - x.score)[0];
    if (custom && custom.score >= 60) {
      const err = await shell.openPath(custom.path);
      if (!err) return { text: `Запустила «${custom.name}».`, label: `🚀 ${custom.name}` };
    }

    // 2) встроенные программы Windows
    if (IS_WIN && BUILTIN_EXE[alias]) {
      const exe = BUILTIN_EXE[alias];
      if (exe.endsWith(':')) await shell.openExternal(exe);
      else await launchDetached(exe).catch(() => shellStart(exe));
      return { text: `Запустила ${raw}.`, label: `🚀 ${raw}` };
    }

    // 3) ярлыки меню «Пуск» и рабочего стола
    const list = startMenuShortcuts();
    const ranked = list
      .map((s) => ({ ...s, score: Math.max(score(raw, s.name), score(alias, s.name)) }))
      .filter((s) => s.score > 0)
      .sort((x, y) => y.score - x.score || x.name.length - y.name.length);
    if (ranked.length && ranked[0].score >= 50) {
      const err = await shell.openPath(ranked[0].path);
      if (!err) return { text: `Запустила «${ranked[0].name}».`, label: `🚀 ${ranked[0].name}` };
    }

    // 4) последняя попытка — через start (безопасные символы)
    if (IS_WIN && /^[\w .\-а-яё]+$/i.test(raw)) {
      const r = await shellStart(raw);
      if (r.code === 0 && !r.stderr.trim()) return { text: `Попробовала запустить «${raw}» через start.`, label: `🚀 ${raw}` };
    }
    const sugg = ranked.slice(0, 5).map((s) => s.name);
    return {
      text: `Не нашла приложение «${raw}».` + (sugg.length ? ` Похожие: ${sugg.join(', ')}.` : '') +
        ' Пользователь может добавить ярлык в Настройки → Приложения.',
      isError: true,
      label: `🚀 не найдено: ${raw}`,
    };
  }

  async t_open_folder(a) {
    const p = String(a.path || '').trim();
    const aliases = {
      desktop: 'desktop', 'рабочий стол': 'desktop', downloads: 'downloads', 'загрузки': 'downloads',
      documents: 'documents', 'документы': 'documents', pictures: 'pictures', 'изображения': 'pictures', 'картинки': 'pictures',
      music: 'music', 'музыка': 'music', videos: 'videos', 'видео': 'videos', home: 'home', 'домашняя': 'home',
    };
    const key = aliases[normalize(p)];
    const target = key ? app.getPath(key) : p;
    if (!fs.existsSync(target)) throw new Error(`Путь не существует: ${target}`);
    const err = await shell.openPath(target);
    if (err) throw new Error(err);
    return { text: `Открыла: ${target}`, label: `📁 ${path.basename(target) || target}` };
  }

  async t_media_control(a) {
    if (!IS_WIN) throw new Error('Управление медиа поддерживается только в Windows');
    const codes = { play_pause: 179, next: 176, previous: 177, volume_up: 175, volume_down: 174, mute: 173 };
    const labels = { play_pause: '⏯️ пауза/play', next: '⏭️ следующий трек', previous: '⏮️ предыдущий трек', volume_up: '🔊 громче', volume_down: '🔉 тише', mute: '🔇 звук' };
    if (a.action === 'set_volume') {
      const level = Math.max(0, Math.min(100, Math.round(Number(a.level ?? 50))));
      await powershell(`$w=New-Object -ComObject WScript.Shell; for($i=0;$i -lt 50;$i++){ $w.SendKeys([char]174) }; for($i=0;$i -lt ${Math.round(level / 2)};$i++){ $w.SendKeys([char]175) }`);
      return { text: `Громкость установлена на ~${level}%.`, label: `🔊 громкость ${level}%` };
    }
    const code = codes[a.action];
    if (!code) throw new Error('Неизвестное действие');
    const times = (a.action === 'volume_up' || a.action === 'volume_down') ? Math.max(1, Math.min(50, Math.round(Number(a.level) || 5))) : 1;
    await sendKeys(code, times);
    return { text: `Готово: ${a.action}${times > 1 ? ' ×' + times : ''}.`, label: labels[a.action] };
  }

  async t_system_action(a) {
    if (!IS_WIN) throw new Error('Доступно только в Windows');
    switch (a.action) {
      case 'lock': await run('rundll32.exe', ['user32.dll,LockWorkStation']); return { text: 'Компьютер заблокирован.', label: '🔒 блокировка' };
      case 'show_desktop': await powershell('(New-Object -ComObject Shell.Application).MinimizeAll()'); return { text: 'Все окна свёрнуты.', label: '🗔 рабочий стол' };
      case 'task_manager': await launchDetached('taskmgr.exe').catch(() => shellStart('taskmgr.exe')); return { text: 'Открыт диспетчер задач.', label: '📊 диспетчер задач' };
      case 'settings': await shell.openExternal('ms-settings:'); return { text: 'Открыты параметры Windows.', label: '⚙️ параметры' };
      default: throw new Error('Неизвестное действие');
    }
  }

  t_clipboard_read() {
    const t = clipboard.readText();
    return { text: t ? truncate(t, 8000) : '(буфер обмена пуст или содержит не текст)', label: '📋 прочитан буфер' };
  }

  t_clipboard_write(a) {
    clipboard.writeText(String(a.text || ''));
    return { text: 'Текст скопирован в буфер обмена.', label: '📋 скопировано' };
  }

  async t_take_screenshot() {
    const shot = await this.d.captureScreen();
    return {
      text: `Скриншот экрана ${shot.width}×${shot.height} (${localIso(Date.now())}).`,
      images: [{ mime: shot.mime, data: shot.data }],
      label: '📸 скриншот экрана',
    };
  }

  async t_run_command(a) {
    const settings = this.d.store.settings;
    if (!settings.capabilities?.runCommands) throw new Error('Выполнение команд отключено в настройках');
    const command = String(a.command || '').trim();
    if (!command) throw new Error('Пустая команда');
    if (settings.capabilities.confirmCommands !== false) {
      const res = await dialog.showMessageBox(this.d.dialogParent?.() || undefined, {
        type: 'warning',
        title: 'Вера хочет выполнить команду',
        message: a.reason ? String(a.reason) : 'Разрешить выполнение команды?',
        detail: command,
        buttons: ['Выполнить', 'Отмена'],
        defaultId: 1,
        cancelId: 1,
        noLink: true,
      });
      if (res.response !== 0) return { text: 'Пользователь отклонил выполнение команды.', isError: true, label: '⛔ команда отклонена' };
    }
    const r = IS_WIN
      ? await powershell(command, { timeout: 60000 })
      : await run('/bin/sh', ['-c', command], { timeout: 60000 });
    const out = [r.stdout && 'STDOUT:\n' + truncate(r.stdout, 6000), r.stderr && 'STDERR:\n' + truncate(r.stderr, 3000)].filter(Boolean).join('\n\n');
    return { text: `Код выхода: ${r.code}\n${out || '(нет вывода)'}`, isError: r.code !== 0, label: `💻 ${command.slice(0, 50)}` };
  }
}

module.exports = { Tools, parseDue };
