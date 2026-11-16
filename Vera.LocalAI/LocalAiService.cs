using Vera.Core;
using Vera.LocalAI.Llm;
using Vera.LocalAI.Voice;

namespace Vera.LocalAI;

/// <summary>
/// Всё офлайновое на ПК в одном месте: папка моделей, встроенная LLM, Whisper, Silero VAD, Vosk.
/// Платформа создаёт один экземпляр после VeraCore и вызывает <see cref="Attach"/>.
/// </summary>
public sealed class LocalAiService : IDisposable
{
    private readonly VeraCore _core;
    private readonly Lazy<SileroVad?> _vad;
    private readonly WhisperTranscriber _whisper = new();

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
        var path = WhisperModelPath() ?? throw new InvalidOperationException(
            $"Модель Whisper «{s.WhisperModel}» не скачана — открой «Настройки → Голос» и скачай её.");
        _whisper.UnloadAfterMinutes = Math.Max(1, _core.Settings.Current.Local.UnloadAfterMinutes);
        return await _whisper.TranscribeAsync(path, wav, s.Language, ct).ConfigureAwait(false);
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
        if (_vad.IsValueCreated) _vad.Value?.Dispose();
    }
}
