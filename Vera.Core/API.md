# Vera.Core — API для платформ (ПК / Android)

Общая библиотека `net10.0`. Платформа создаёт **один** `VeraCore` и строит UI поверх его сервисов.
Исходники короткие — при сомнениях читайте код в `Vera.Core/`.

## Создание

```csharp
var core = new VeraCore(storage, secrets, platformTools, platform: "desktop" /* или "mobile" */);
core.Reminders.Start();           // ПК: внутренний таймер 5 с. Android: см. ниже
```

Платформа реализует (`Vera.Core/Abstractions.cs`):

| Интерфейс | Что сделать |
|---|---|
| `IVeraStorage` | `ReadText(name)` / `WriteText(name, content)` — файлы в папке данных (ПК: `%APPDATA%\VeraAssistant`, Android: `FileSystem.AppDataDirectory`). Запись атомарная (tmp + move). |
| `ISecretStore` | `Get/Set` ключей API. ПК: DPAPI (`ProtectedData`, CurrentUser). Android: `SecureStorage`. `Set(name, null)` — удалить. |
| `IPlatformTools` | Инструменты устройства: `GetDefinitions(settings)` + `TryExecuteAsync(name, args, ct)` (вернуть `null`, если имя не ваше). Схемы — через `ToolDefinition.Schema/Str/Num/Enum`. Аргументы читать `args.Str("x")`, `args.Num("x")`. Возвращать `ToolResult.Ok(text, label)` / `ToolResult.Error(...)`; картинки (скриншот) — `new ToolResult { Text, Images = [new ImageData("image/jpeg", base64)], Label }`. |

Общие инструменты ядра (их **не** надо реализовывать): `create_reminder`, `list_reminders`, `delete_reminder`, `update_reminder`, `remember`, `forget`, `get_weather`.

## Сервисы `VeraCore`

| Свойство | Главное |
|---|---|
| `Settings` (`SettingsService`) | `Current` (`VeraSettings`, только чтение), `Update(s => s.Pet.Scale = 4)`, событие `Changed`. Модель настроек — `Vera.Core/Settings.cs`. |
| `Secrets` (`SecretsService`) | `Status()` → список `SecretStatus(Name, Title, IsSet, Hint, Purpose, Url)`, `Set(name, value)`, `Has(name)`, событие `Changed`. Имена: `openrouter`, `anthropic`, `openai`, `groq`, `elevenlabs`. |
| `Agent` (`VeraAgent`) | `SendAsync(text, images?, source: "chat"/"voice"/"screen", hidden?)`, `Stop()`, `IsBusy`, `GetVisibleHistory()`, `ClearHistory()`, событие `Event` (**фоновый поток!** — маршалить в UI). |
| `Reminders` (`ReminderService`) | `List()`, `Upcoming()`, `Add(text, due, repeat, source)`, `Remove`, `Update`, `Snooze(id, min)`, `Complete(id)`, `Tick(startup)`, `Start()`. События `Changed`, `Due(reminder, missed)`. `ReminderService.Repeats`, `RepeatLabel()`. |
| `Memory` (`MemoryService`) | `List()`, `Add`, `Update`, `Remove`, `Clear`, событие `Changed`. |
| `Speech` (`SpeechService`) | `TranscribeAsync(wavBytes, "audio/wav")` (OpenAI / Groq), `SynthesizeAsync(text)` → `SpeechAudio { Audio (mp3) | UseSystemVoice+Text | Skip }`, `ElevenVoicesAsync()`, `SpeechService.CleanForSpeech(text)`. |
| `Providers` (`ProviderRegistry`) | `ListModelsAsync(provider)` → `ModelInfo(Id, Name, Tools, Vision)`; `ProviderRegistry.Known` — id/название/подсказка. |
| `Line(kind, text?)` | Готовые реплики характера: `poke`, `drag`, `idle`, `listening`, `notHeard`, `reminder`, `missed`, `noKey`, `greet*`. |
| `Greeting()` | `(Text, NoKey)` — приветствие при запуске. |
| `ReminderPhrase(r, missed)` | Что сказать при срабатывании напоминания. |
| `TestKeyAsync(name)` | Проверка ключа → строка «Ключ работает …» или исключение с понятным текстом. |
| `Flush()` | Сохранить всё перед выходом. |

### События агента (`Vera.Core.Agent`)

```
TurnStarted(TurnId, Source, UserText, HasImage, Hidden)
TextDelta(TurnId, Step, Delta)            — текст ответа по кусочкам (тег эмоции уже вырезан)
EmotionChanged(TurnId, Emotion)           — neutral|smug|happy|laugh|think|surprised|angry|shy|sad|sleepy
ToolActivity(TurnId, CallId, Name, Running, Label, IsError, Kind)   — Kind "reminder" → анимация ⏰
TurnEnded(TurnId, Text, Emotion, Source)
TurnFailed(TurnId, Message, Aborted, PartialText, Source)
```
Когда `Step` меняется — это новый абзац (между шагами модели были вызовы инструментов).

Звук ответа (v2.5, вместо синтеза речи): на `TurnStarted` — `pacer.Reset()`, каждый `TextDelta` → `BlipPacer.Push(delta)`,
таймер ~15 мс: `if (pacer.Tick(now)) play(ReplySound.Blip(settings.Sound.Style, ReplySound.PitchFor(persona), rng, ReplySound.Gain(settings.Sound.Volume)))`.
Звук — моно float PCM 44,1 кГц; «off» даёт пустой массив.

### Голосовой ввод (`Vera.Core.Speech.VoiceRecorder`)

Платформа читает микрофон (моно, любая частота, кадры 20–50 мс, float −1..1) и зовёт `recorder.Feed(samples, sampleRate)`.
`StartCommand()` — одна фраза (кнопка/горячая клавиша), `FinishNow()` — закончить досрочно, `StartWake()/StopWake()` —
постоянное прослушивание, `Paused = true` пока Вера говорит. События: `Segment(wavBytes16k, mode, ms)`, `Level(0..1)`,
`StateChanged(state, reason)`. Для режима Wake: `VoiceRecorder.MatchWake(transcript, settings.Voice.WakePhrases)` →
текст команды после «Вера» / `""` (только позвали) / `null` (не нам).

### Спрайт (`Vera.Core.Sprites.VeraSprite`)

Слои PNG 64×72 лежат в `assets/vera/` (генерирует `tools/gen_sprite.py`). Масштабировать **без сглаживания**.
`VeraSprite.Compose(emotion, mode, blink, mouthOpen, dragging, sitting, legFrame)` → `SpriteFrame.Layers` — имена файлов
по порядку отрисовки. `sitting: true` — сидячая поза для мобильного чата: строка `VeraSprite.SeatLine` (62) совпадает
с верхним краем панели, ноги (`legs_sit_0..2`) свисают поверх неё. Моргание — каждые 2–6 с на 120 мс; рот открыт по уровню звука > 0.18.

### Markdown (`Vera.Core.Markdown.MarkdownLite`)

`Parse(md)` → блоки (`Paragraph`, `Heading`, `Code`, `Bullet`, `Numbered`, `Quote`, `Rule`) с inline-элементами
(`Text`, `Bold`, `Italic`, `Code`, `Link`, `Strike`) — рисуйте родными контролами. `ToPlain(md)` — текст без разметки.

### Прочее
`RuText.FormatDateTime / WhenShort / Relative` — русские даты. `Personas.All` — характеры (`Label`, `TtsHint`).
Ошибки провайдеров — `ProviderException` с готовым текстом для пользователя.

## Что добавлено в v2.2

- **`TextReplaced(TurnId, Step, Text)`** — текст шага заменён целиком (модель написала вызов инструмента JSON-ом,
  его выполнили и убрали из ответа). UI заменяет текст этого шага.
- **`AgentBusyException`** (наследник `InvalidOperationException`) — `SendAsync`, пока предыдущий ход не закончен.
- **`SendAsync(..., documents)`** — документы из `DocumentReader.Read(bytes, fileName)` (PDF, DOCX, TXT/MD/CSV, код).
  Длинные режутся, модель дочитывает их инструментом `read_document`.
- **Разбор вызовов из текста** (`ToolCallParser`, настройка `ToolCallFallback`). Если модель отвергла нативные
  инструменты, они автоматически описываются в промпте (`ToolPrompt`).
- **Провайдеры:**
  - `custom` — свой OpenAI-совместимый сервер (`CustomBaseUrl`);
  - `ProviderRegistry.Register(provider)` — провайдер платформы (на ПК — `local`);
  - `ProviderRegistry.Available`, `NeedsKey(id)`; `VeraCore.ProviderReady`.
- **Инструменты `web_search` / `read_page`.** Настройки `Search.Enabled` и `Search.Engine`, ключи `tavily` и `brave`.
- **Расход и журнал:**
  - `VeraCore.Usage` — токены и примерная стоимость (`Today()`, `ThisMonth()`, `MonthByProvider()`);
  - `VeraCore.RequestLog` — журнал запросов (`Diagnostics.LogRequests`).
- **Голос:**
  - `SpeechService.LocalTranscriber` (`stt.provider == "whisper"`) задаёт платформа;
  - `SpeechAudio.RateApplied` — скорость уже учтена сервисом, звук растягивать не нужно.
- **`VoiceRecorder`:**
  - `SpeechProbability` — нейросетевой VAD вместо порога громкости;
  - `EndProcessing()` — вернуть состояние `Off`/`Wake` после распознавания.
- **Спрайт:**
  - `VeraSprite.Compose(..., Posture posture, legFrame)` — `Stand` | `Bar` | `Floor` (слои `floor_<pose>`);
  - `Skins.All`, `Skins.Get(id)`, `Skins.HaloPose(emotion, mode, posture)` — покадровый скин «halo» (`assets/skins/halo`).
- **ПК:** `Pet.X` / `Pet.Y` хранятся в **физических пикселях** экрана (не в DIP).

## Локальный ИИ на ПК (`Vera.LocalAI`, только Windows)

```csharp
var local = new LocalAiService(core, modelsRoot, sileroPath);  // после VeraCore
local.Attach();   // провайдер "local" + Speech.LocalTranscriber (Whisper)
recorder.SpeechProbability = local.VadHook();                  // Silero вместо порога громкости
var wake = local.CreateWakeWord();                             // Vosk; null — модель не скачана
```
- `local.Store` (`ModelStore`):
  - `InstallAsync(asset, progress, ct)` — докачка через Range, распаковка zip/nupkg;
  - `IsInstalled`, `Delete`, `PathOf`, `UsedBytes()`.
- `ModelCatalog.Llms / Voice / Backends` — что можно скачать (URL, размер, качество команд и русского).
- `local.Hardware` — память, ядра, AVX2, Vulkan/NVIDIA; `RecommendedLlm()`, `RecommendedWhisper()`.
- `local.Llm` (`LocalLlmProvider`):
  - `Status` — «Загружаю модель… 40%»;
  - `TestAsync()`, `ResolveModelPath(model)`;
  - `Engine.Unload()`.
- Модель "local" в `Settings.Models["local"]` — полный путь к `.gguf` (или id из каталога).
- Ускоритель (`Local.Backend`) выбирается один раз за запуск: `LlamaEngine.BackendLocked`, `ActiveBackend`.
- Настройки: `SettingsService.Export()` / `Import(json)` / `Reset()`; наборы — `SettingsPresets.All`, `MissingKeys(id, has)`.

## Синхронизация (`Vera.Core.Sync`, v2.3)

- `core.Sync` (`SyncService`): `DeviceId`, `DeviceName`, `Peers`, события `Changed`, `Synced`;
  `LastActive` — платформа сообщает, когда пользователь был активен.
- **ПК:**
  - `new SyncServer(core.Sync).Start(port)`;
  - `StartPairing(SyncNet.LocalAddresses(), port)` → `PairingOffer.Uri` для QR;
  - `Unpair(id)`.
- **Телефон:**
  - `await core.Sync.PairAsync(qrText)` — привязка и первая синхронизация;
  - `await core.Sync.SyncAsync(peer)` — при открытии, по кнопке, в фоне;
  - ПК ищется по адресам из QR, потом `SyncNet.DiscoverAsync`.
- **Напоминания:** `core.Sync.ShouldRingHere(now)` — звенеть ли здесь (настройка `Sync.RingOn`).
- **Чат:** ходы приходят в историю агента, событие `Agent.HistoryChanged` — перечитать чат.
- **Скины:**
  - `Skins.Pose(skin, emotion, mode, posture, dragging, mouthOpen)`;
  - `FramePose.Folder` — поза из другого скина;
  - `Skins.AccentFor(skin)` — тема "gold" | "pink".

## Язык интерфейса (L10n)

- Ключ словаря — сама русская строка, значение — английская: `Vera.Core/L10n/en.json` (встроен в сборку).
- `L10n.T("Настройки")` → "Settings" в английском режиме, иначе исходная строка; неизвестная строка возвращается как есть.
- `L10n.F("Дела · {0}", n)` — то же с подстановками; плейсхолдеры `{0}`, `{name}` в переводе обязаны совпадать.
- `L10n.Lang` ставит `VeraCore` из `Settings.Language` ("ru" | "en") при запуске; смена языка — после перезапуска.
- Старые установки без поля `language` остаются на русском, новые берут язык системы (`L10n.SystemDefault()`).
- Тест `EveryExplicitKeyHasTranslation` падает, если в коде есть `L10n.T/F("…")` без перевода.
- Новая строка интерфейса: оберни в `L10n.T` (или передай в `Ui.*`, они переводят сами) и добавь пару в `en.json`.

## Свои команды (конструктор, ПК)

- Модель: `UserCommand` (Nodes + Links), `CommandNode` (Type, X, Y, Props), `CommandLink` (From, Port, To). Хранение — `commands.json`, `Core.Commands` (`CommandService`), не синхронизируется.
- Каталог блоков — `Blocks.All` (категория, иконка, свойства, выходы, подпись); `Blocks.Search(q)` для меню, `Blocks.Create(type, x, y)`.
- Выполнение — `new CommandRunner(Core.Tools.ExecuteAsync, Core.Reminders, ICommandHost)`; события `StepStarted` / `StepFinished` для подсветки. Действия идут через те же инструменты, что у Веры в чате.
- Платформа реализует `ICommandHost`: `AskAsync` (да/нет), `Say` (реплика), `AskAiAsync` (вопрос в чат).
- Запуск: `MatchPhrase(text)` — только точное совпадение фразы; `DueSchedules(now)` — раз в день в окне 5 минут; `StartupCommands()`, `Hotkeys()`.
- Вера: `Core.Tools.RunCommand` задаёт платформа — тогда у модели появляется инструмент `run_saved_command` со списком команд, где `AllowAi`.

## Вайбкодинг (ПК)

- `ProjectWorkspace(root)` — папка «Проекты Веры»: `Projects/Create/Files/Read/Write/Replace/Delete`, все пути через `Resolve` (никаких «..», дисков, `.vera`, ссылок наружу).
- История: каждый `Write/Replace/Delete` — шаг в `.vera/journal.json` с копиями прежних версий; `Undo(project)` откатывает последний; `SnapshotAll` — снимок всего проекта перед работой агента (новые файлы откат удалит).
- `CodingTools(workspace, ICodingHost, settings)` → `Core.Tools.Coding`: инструменты `code_projects / code_create_project / code_list_files / code_read_file / code_write_file / code_replace / code_run / code_open / code_undo`, `code_delegate` — если выбран агент и он найден. Событие `Activity(project, text)` — для журнала и пузыря.
- Платформа реализует `ICodingHost`: `RunAsync` (команда в папке проекта после подтверждения), `Open`, `AgentProblem`, `RunAgentAsync`.
- `ClaudeCodeAgent.Args(allowCommands)` — `claude -p --output-format stream-json --verbose --permission-mode acceptEdits [--allowedTools Bash]`, задача — через stdin; `Parse(line)` → шаг или итог.
- С инструментами кода у агента до 40 шагов за ход (обычно 10) и подсказка в системном промпте.
