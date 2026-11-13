using System.Text.Json;
using Vera.Core.Speech;
using Vosk;

namespace Vera.LocalAI.Voice;

/// <summary>
/// Постоянное прослушивание имени «Вера» офлайн (Vosk, маленькая русская модель). Распознаватель ограничен
/// грамматикой из слов-имён и «[unk]», поэтому лёгкий и почти не ошибается на посторонней речи.
/// Платформа кормит его кадрами микрофона; при срабатывании — событие <see cref="Detected"/>.
/// </summary>
public sealed class VoskWakeWord : IDisposable
{
    private const int Rate = 16000;
    private static bool _logSet;

    private readonly Model _model;
    private readonly object _gate = new();
    private VoskRecognizer _rec;
    private string[] _words;
    private DateTime _cooldownUntil = DateTime.MinValue;

    public VoskWakeWord(string modelDir, string wakePhrases)
    {
        if (!_logSet) { Vosk.Vosk.SetLogLevel(-1); _logSet = true; }
        _model = new Model(modelDir);
        _words = Words(wakePhrases);
        _rec = Create();
    }

    /// <summary>Сработало: слово, которое услышали.</summary>
    public event Action<string>? Detected;

    /// <summary>Не срабатывать повторно столько времени после срабатывания.</summary>
    public TimeSpan Cooldown { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Сменить слова (из настроек «Слова для вызова»).</summary>
    public void SetPhrases(string wakePhrases)
    {
        lock (_gate)
        {
            var w = Words(wakePhrases);
            if (w.SequenceEqual(_words)) return;
            _words = w;
            _rec.Dispose();
            _rec = Create();
        }
    }

    private VoskRecognizer Create()
    {
        var grammar = JsonSerializer.Serialize(_words.Append("[unk]").ToArray());
        var r = new VoskRecognizer(_model, Rate, grammar);
        r.SetMaxAlternatives(0);
        r.SetWords(false);
        return r;
    }

    /// <summary>«вера, верочка, эй вера» → ["вера","верочка","эй"…] — Vosk-грамматике нужны отдельные слова.</summary>
    internal static string[] Words(string phrases) =>
        (phrases ?? "").ToLowerInvariant().Replace('ё', 'е')
            .Split(new[] { ',', ';', ' ', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => w.Length >= 3 && w is not "эй" and not "hey")
            .Distinct()
            .DefaultIfEmpty("вера")
            .ToArray();

    /// <summary>Кадр с микрофона (моно float −1..1, любая частота).</summary>
    public void Feed(float[] samples, int sampleRate)
    {
        var s16 = sampleRate == Rate ? samples : WavEncoder.Downsample(samples, sampleRate, Rate);
        var pcm = new short[s16.Length];
        for (var i = 0; i < s16.Length; i++) pcm[i] = (short)Math.Clamp(s16[i] * 32767f, short.MinValue, short.MaxValue);

        string? hit = null;
        lock (_gate)
        {
            var final = _rec.AcceptWaveform(pcm, pcm.Length);
            var json = final ? _rec.Result() : _rec.PartialResult();
            var text = Extract(json, final ? "text" : "partial");
            if (text.Length > 0 && DateTime.UtcNow >= _cooldownUntil)
            {
                hit = Match(text, _words);
                if (hit is not null)
                {
                    _cooldownUntil = DateTime.UtcNow + Cooldown;
                    _rec.Reset();
                }
            }
        }
        if (hit is not null) Detected?.Invoke(hit);
    }

    public void Reset()
    {
        lock (_gate) _rec.Reset();
    }

    internal static string Extract(string json, string field)
    {
        try
        {
            using var d = JsonDocument.Parse(json);
            return d.RootElement.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
        }
        catch (JsonException) { return ""; }
    }

    /// <summary>Есть ли в распознанном тексте одно из слов (а не только «[unk]»).</summary>
    internal static string? Match(string text, IReadOnlyCollection<string> words) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(words.Contains);

    public void Dispose()
    {
        lock (_gate) _rec.Dispose();
        _model.Dispose();
    }
}
