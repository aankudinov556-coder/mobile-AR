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

Озвучка: если `Source == "voice"` и `tts.autoSpeak == "voice"` (или `"always"`), кормите `TextDelta` в
`new SentenceChunker(chunk => queue(chunk))`, в конце хода `End()`; каждый кусок → `Speech.SynthesizeAsync` → проиграть.

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
  - `SpeechService.LocalTranscriber` (`stt.provider == "whisper"`) и `LocalSynthesizer` (`tts.engine == "piper"`) задаёт платформа;
  - `SpeechAudio.RateApplied` — скорость уже учтена сервисом, звук растягивать не нужно.
- **`VoiceRecorder`:**
  - `SpeechProbability` — нейросетевой VAD вместо порога громкости;
  - `EndProcessing()` — вернуть состояние `Off`/`Wake` после распознавания.
- **Спрайт:**
  - `VeraSprite.Compose(..., Posture posture, legFrame)` — `Stand` | `Bar` | `Floor` (слои `floor_<pose>`);
  - `Skins.All`, `Skins.Get(id)`, `Skins.HaloPose(emotion, mode, posture)` — покадровый скин «halo» (`assets/skins/halo`).
- **ПК:** `Pet.X` / `Pet.Y` хранятся в **физических пикселях** экрана (не в DIP).
