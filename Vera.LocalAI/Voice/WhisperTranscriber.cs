using System.Text;
using Whisper.net;

namespace Vera.LocalAI.Voice;

/// <summary>
/// Распознавание речи офлайн: whisper.cpp через Whisper.net. Модель (ggml-*.bin) загружается при первой фразе
/// и выгружается после простоя. Вход — WAV 16 кГц моно (как отдаёт VoiceRecorder).
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private WhisperFactory? _factory;
    private string? _loadedPath;

    public WhisperTranscriber() => _idle = new Timer(_ => Unload());

    /// <summary>Выгружать модель после стольких минут без речи.</summary>
    public int UnloadAfterMinutes { get; set; } = 10;

    /// <summary>Подсказка для распознавания (имена, термины) — помогает Whisper писать «Вера» правильно.</summary>
    public string Prompt { get; set; } = "Вера, напомни, открой, поставь, браузер, YouTube, Telegram.";

    public async Task<string> TranscribeAsync(string modelPath, byte[] wav, string language, CancellationToken ct)
    {
        if (!File.Exists(modelPath)) throw new FileNotFoundException("Модель Whisper не скачана", modelPath);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        _idle.Change(Timeout.Infinite, Timeout.Infinite);
        try
        {
            if (_factory is null || _loadedPath != modelPath)
            {
                _factory?.Dispose();
                _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), ct).ConfigureAwait(false);
                _loadedPath = modelPath;
            }
            var b = _factory.CreateBuilder()
                .WithThreads(Math.Clamp(Environment.ProcessorCount - 1, 2, 8))
                .WithNoContext()
                .WithSingleSegment();
            b = string.IsNullOrWhiteSpace(language) || language == "auto" ? b.WithLanguageDetection() : b.WithLanguage(language);
            if (!string.IsNullOrWhiteSpace(Prompt)) b = b.WithPrompt(Prompt);
            await using var processor = b.Build();
            using var ms = new MemoryStream(wav);
            var sb = new StringBuilder();
            await foreach (var seg in processor.ProcessAsync(ms, ct).ConfigureAwait(false)) sb.Append(seg.Text);
            return Clean(sb.ToString());
        }
        finally
        {
            if (UnloadAfterMinutes > 0) _idle.Change(TimeSpan.FromMinutes(UnloadAfterMinutes), Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>Whisper на тишине любит «галлюцинировать» титры — выкидываем типичные.</summary>
    internal static string Clean(string text)
    {
        var t = text.Trim();
        string[] junk =
        {
            "Продолжение следует", "Субтитры сделал", "Субтитры создавал", "Редактор субтитров", "Спасибо за просмотр",
            "[музыка]", "[Музыка]", "(музыка)", "[BLANK_AUDIO]", "Подписывайтесь на канал",
        };
        foreach (var j in junk)
            if (t.Contains(j, StringComparison.OrdinalIgnoreCase) && t.Length < j.Length + 25) return "";
        return t.Trim('[', ']', ' ');
    }

    public void Unload()
    {
        if (!_gate.Wait(0)) return; // идёт распознавание — выгрузим в следующий раз
        try
        {
            _factory?.Dispose();
            _factory = null;
            _loadedPath = null;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _idle.Dispose();
        _factory?.Dispose();
        _factory = null;
    }
}
