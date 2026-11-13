using Vera.Core;
using Vera.Core.Speech;
using Vera.LocalAI.Llm;
using Vera.LocalAI.Voice;

namespace Vera.LocalAI;

/// <summary>
/// Всё офлайновое на ПК в одном месте: папка моделей, встроенная LLM, Whisper, Piper, Silero VAD, Vosk.
/// Платформа создаёт один экземпляр после VeraCore и вызывает <see cref="Attach"/>.
/// </summary>
public sealed class LocalAiService : IDisposable
{
    private readonly VeraCore _core;
    private readonly Lazy<SileroVad?> _vad;
    private readonly WhisperTranscriber _whisper = new();
    private PiperTts? _piper;

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

    /// <summary>Зарегистрировать провайдер "local" и подключить офлайн-голос к SpeechService.</summary>
    public void Attach()
    {
        _core.Providers.Register(Llm);
        _core.Speech.LocalTranscriber = TranscribeAsync;
        _core.Speech.LocalSynthesizer = SynthesizeAsync;
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

    // ---------- Piper ----------

    public bool PiperReady(out string reason)
    {
        var voice = ModelCatalog.PiperVoiceFor(_core.Settings.Current.Tts.PiperVoice);
        if (!File.Exists(Store.PiperExe)) { reason = "Piper не установлен"; return false; }
        if (voice is null || !Store.IsInstalled(voice)) { reason = "голос Piper не скачан"; return false; }
        reason = "";
        return true;
    }

    private async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct)
    {
        var t = _core.Settings.Current.Tts;
        if (!PiperReady(out _))
            return new SpeechAudio { UseSystemVoice = true, Text = text }; // ещё не скачан — говорим голосом Windows
        _piper ??= new PiperTts(Store.PiperExe, Path.Combine(Path.GetTempPath(), "VeraPiper"));
        var voice = Store.PathOf(ModelCatalog.PiperVoiceFor(t.PiperVoice)!);
        try
        {
            var wav = await _piper.SynthesizeAsync(voice, text, t.Rate, ct).ConfigureAwait(false);
            return wav.Length == 0 ? new SpeechAudio { Skip = true } : new SpeechAudio { Audio = wav, Mime = "audio/wav", RateApplied = true };
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // не молчим: если Piper сломался — системный голос
            return new SpeechAudio { UseSystemVoice = true, Text = text };
        }
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
        _piper?.Dispose();
        if (_vad.IsValueCreated) _vad.Value?.Dispose();
    }
}
