using Vera.Core;
using Vera.Core.Speech;
using Vera.LocalAI.Llm;
using Vera.LocalAI.Voice;

namespace Vera.LocalAI;

/// <summary>
/// Всё офлайновое на ПК в одном месте: папка моделей, встроенная LLM, Whisper, потоковое распознавание (sherpa-onnx), Silero VAD, Vosk.
/// Платформа создаёт один экземпляр после VeraCore и вызывает <see cref="Attach"/>.
/// </summary>
public sealed class LocalAiService : IDisposable
{
    private readonly VeraCore _core;
    private readonly Lazy<SileroVad?> _vad;
    private readonly WhisperTranscriber _whisper = new();
    private readonly object _streamGate = new();
    private StreamingSpeech? _stream;

    /// <param name="core">ядро</param>
    /// <param name="modelsRoot">папка моделей (ПК: %LOCALAPPDATA%\VeraAssistant\models)</param>
    /// <param name="sileroPath">silero_vad.onnx, который идёт вместе с программой</param>
    /// <param name="engine">движок LLM (по умолчанию llama.cpp)</param>
    public LocalAiService(VeraCore core, string modelsRoot, string sileroPath, ILocalEngine? engine = null)
    {
        _core = core;
        Store = new ModelStore(modelsRoot, CreateDownloadClient());
        Hardware = HardwareInfo.Detect();
        Llm = new LocalLlmProvider(engine ?? new LlamaEngine(Store.BackendsDir), Store, () => core.Settings.Current,
            id => core.Providers.IsAvailable(id) && core.Secrets.Has(id) ? core.Providers.Get(id) : null);
        SileroPath = sileroPath;
        _vad = new Lazy<SileroVad?>(() =>
        {
            try { return File.Exists(sileroPath) ? new SileroVad(sileroPath) : null; }
            catch (Exception e) { VadError = e.Message; return null; }
        });
    }

    public ModelStore Store { get; }
    public HardwareInfo Hardware { get; }
    public LocalLlmProvider Llm { get; }
    public string SileroPath { get; }
    /// <summary>Почему не загрузился Silero (если не загрузился).</summary>
    public string? VadError { get; private set; }

    private static HttpClient CreateDownloadClient()
    {
        var h = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None })
        {
            Timeout = Timeout.InfiniteTimeSpan, // большие файлы — таймаут через CancellationToken
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("VeraAssistant/2.2");
        return h;
    }

    /// <summary>Зарегистрировать провайдер "local" и подключить офлайн-распознавание к SpeechService.</summary>
    public void Attach()
    {
        _core.Providers.Register(Llm);
        _core.Speech.LocalTranscriber = TranscribeAsync;
    }

    // ---------- Whisper ----------

    public string? WhisperModelPath()
    {
        var a = ModelCatalog.WhisperFor(_core.Settings.Current.Stt.WhisperModel);
        return a is not null && Store.IsInstalled(a) ? Store.PathOf(a) : null;
    }

    private async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        var s = _core.Settings.Current.Stt;
        if (s.Provider == "stream")
        {
            var rec = Streaming() ?? throw new InvalidOperationException(L10n.T("Модель быстрого распознавания не скачана — открой «Настройки → Голос и слух» и скачай её."));
            var (samples, rate) = WavEncoder.Decode(wav);
            return await Task.Run(() => rec.Transcribe(samples, rate), ct).ConfigureAwait(false);
        }
        var path = WhisperModelPath() ?? throw new InvalidOperationException(
            $"Модель Whisper «{s.WhisperModel}» не скачана — открой «Настройки → Голос» и скачай её.");
        _whisper.UnloadAfterMinutes = Math.Max(1, _core.Settings.Current.Local.UnloadAfterMinutes);
        return await _whisper.TranscribeAsync(path, wav, s.Language, ct).ConfigureAwait(false);
    }

    // ---------- потоковое распознавание ----------

    /// <summary>Язык распознавания: из настроек, при «авто» — язык интерфейса.</summary>
    public string StreamLanguage()
    {
        var l = _core.Settings.Current.Stt.Language;
        return l is "ru" or "en" ? l : (L10n.English ? "en" : "ru");
    }

    public AssetInfo StreamAsset() => ModelCatalog.StreamFor(StreamLanguage())!;

    public bool StreamReady() => Store.IsInstalled(StreamAsset());

    /// <summary>Распознаватель для текущего языка (null — модель не скачана). Грузится один раз, ~2 с.</summary>
    public StreamingSpeech? Streaming()
    {
        var a = StreamAsset();
        if (!Store.IsInstalled(a)) return null;
        var dir = Store.PathOf(a);
        lock (_streamGate)
        {
            if (_stream is not null && _stream.ModelDir == dir) return _stream;
            _stream?.Dispose();
            _stream = null;
            _stream = new StreamingSpeech(dir, Math.Clamp(Environment.ProcessorCount / 4, 1, 4));
            return _stream;
        }
    }

    /// <summary>Распознаватель, если он уже загружен для текущего языка (не блокирует; иначе запускает загрузку в фоне).</summary>
    public StreamingSpeech? StreamingIfLoaded()
    {
        lock (_streamGate)
        {
            if (_stream is not null && StreamReady() && _stream.ModelDir == Store.PathOf(StreamAsset())) return _stream;
        }
        WarmUpStreaming();
        return null;
    }

    /// <summary>Загрузить модель заранее (в фоне), чтобы первая фраза не ждала.</summary>
    public void WarmUpStreaming()
    {
        if (_core.Settings.Current.Stt.Provider != "stream") return;
        _ = Task.Run(() =>
        {
            try { Streaming(); } catch (Exception) { /* покажем при первой фразе */ }
        });
    }

    // ---------- Silero VAD ----------

    /// <summary>Хук для VoiceRecorder.SpeechProbability (null — Silero недоступен, будет порог громкости).</summary>
    public Func<float[], int, float>? VadHook() => _vad.Value is { } v ? v.Probability : null;

    public void ResetVad() { if (_vad.IsValueCreated) _vad.Value?.Reset(); }

    // ---------- Vosk ----------

    /// <summary>Создать слушатель имени (null — модель Vosk не скачана).</summary>
    public VoskWakeWord? CreateWakeWord()
    {
        var a = ModelCatalog.Get("vosk-small-ru")!;
        if (!Store.IsInstalled(a)) return null;
        return new VoskWakeWord(Store.PathOf(a), _core.Settings.Current.Voice.WakePhrases);
    }

    public void Dispose()
    {
        Llm.Dispose();
        _whisper.Dispose();
        lock (_streamGate) _stream?.Dispose();
        if (_vad.IsValueCreated) _vad.Value?.Dispose();
    }
}
