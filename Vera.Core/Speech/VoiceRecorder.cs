namespace Vera.Core.Speech;

/// <summary>Кодирование PCM в WAV (16 бит, моно).</summary>
public static class WavEncoder
{
    public static byte[] Encode(ReadOnlySpan<float> samples, int sampleRate)
    {
        var data = samples.Length * 2;
        var buf = new byte[44 + data];
        void W(int o, string s) { for (var i = 0; i < s.Length; i++) buf[o + i] = (byte)s[i]; }
        void U32(int o, int v) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); buf[o + 2] = (byte)(v >> 16); buf[o + 3] = (byte)(v >> 24); }
        void U16(int o, int v) { buf[o] = (byte)v; buf[o + 1] = (byte)(v >> 8); }
        W(0, "RIFF"); U32(4, 36 + data); W(8, "WAVE");
        W(12, "fmt "); U32(16, 16); U16(20, 1); U16(22, 1); U32(24, sampleRate); U32(28, sampleRate * 2); U16(32, 2); U16(34, 16);
        W(36, "data"); U32(40, data);
        var o2 = 44;
        foreach (var f in samples)
        {
            var c = Math.Clamp(f, -1f, 1f);
            var v = (short)(c < 0 ? c * 32768 : c * 32767);
            buf[o2++] = (byte)v;
            buf[o2++] = (byte)(v >> 8);
        }
        return buf;
    }

    public static float[] Downsample(ReadOnlySpan<float> src, int from, int to)
    {
        if (from == to) return src.ToArray();
        var ratio = (double)from / to;
        var len = (int)(src.Length / ratio);
        var outArr = new float[len];
        double pos = 0;
        for (var i = 0; i < len; i++)
        {
            var next = (int)((i + 1) * ratio);
            double sum = 0; var n = 0;
            for (var j = (int)pos; j < next && j < src.Length; j++) { sum += src[j]; n++; }
            outArr[i] = n > 0 ? (float)(sum / n) : 0;
            pos = next;
        }
        return outArr;
    }

    public static float Rms(ReadOnlySpan<float> s)
    {
        if (s.Length == 0) return 0;
        double acc = 0;
        foreach (var f in s) acc += f * f;
        return (float)Math.Sqrt(acc / s.Length);
    }
}

public enum ListenMode { Off, Wake, Command }

/// <summary>Состояние записи для анимаций и подсказок.</summary>
public enum RecorderState { Off, Wake, Listening, Recording, Processing }

/// <summary>
/// Детектор речи (VAD) поверх потока PCM. Платформа кормит его кадрами с микрофона (Feed),
/// он сам решает, где фраза началась и закончилась, и отдаёт готовый WAV 16 кГц.
/// Режим Command — одна фраза по кнопке/горячей клавише; Wake — постоянное прослушивание фраз.
/// </summary>
public sealed class VoiceRecorder
{
    private readonly Func<double> _sensitivity;
    private readonly Func<int> _silenceMs;
    private readonly List<float[]> _pre = new();
    private List<float[]>? _rec;
    private double _loudMs;
    private double _silentMs;
    private double _recMs;
    private double _sinceCommandMs;
    private double _noise = 0.008;

    public VoiceRecorder(Func<double> sensitivity, Func<int> silenceMs)
    {
        _sensitivity = sensitivity;
        _silenceMs = silenceMs;
    }

    public ListenMode Mode { get; private set; } = ListenMode.Off;
    /// <summary>Режим, в который вернуться после команды (Wake, если включена активация по слову).</summary>
    public bool WakeEnabled { get; set; }
    /// <summary>Пауза прослушивания (например, пока Вера говорит сама).</summary>
    public bool Paused { get; set; }

    /// <summary>Готовая фраза: WAV 16 кГц моно, режим, длительность в мс.</summary>
    public event Action<byte[], ListenMode, double>? Segment;
    /// <summary>Уровень громкости 0..1 (для индикатора).</summary>
    public event Action<float>? Level;
    /// <summary>Состояние; второй аргумент — причина ("timeout", "short", "cancel") или null.</summary>
    public event Action<RecorderState, string?>? StateChanged;

    public double Threshold
    {
        get
        {
            var sens = Math.Clamp(_sensitivity(), 0, 1);
            var baseThr = 0.055 - sens * 0.045; // 0.055 .. 0.01
            return Math.Max(baseThr, _noise * 2.6);
        }
    }

    public void StartWake()
    {
        WakeEnabled = true;
        if (Mode != ListenMode.Command) { Mode = ListenMode.Wake; StateChanged?.Invoke(RecorderState.Wake, null); }
    }

    public void StopWake()
    {
        WakeEnabled = false;
        if (Mode == ListenMode.Wake) { Mode = ListenMode.Off; _rec = null; StateChanged?.Invoke(RecorderState.Off, null); }
    }

    public void StartCommand()
    {
        Mode = ListenMode.Command;
        _rec = null;
        _loudMs = 0;
        _sinceCommandMs = 0;
        StateChanged?.Invoke(RecorderState.Listening, null);
    }

    /// <summary>Закончить фразу досрочно (повторное нажатие кнопки).</summary>
    public void FinishNow()
    {
        if (Mode != ListenMode.Command) return;
        if (_rec is { Count: > 0 }) Finish(16000);
        else Cancel("cancel");
    }

    public void Cancel(string reason)
    {
        _rec = null;
        Mode = WakeEnabled ? ListenMode.Wake : ListenMode.Off;
        StateChanged?.Invoke(Mode == ListenMode.Wake ? RecorderState.Wake : RecorderState.Off, reason);
    }

    private int _rate = 16000;

    /// <summary>Кадр с микрофона (моно float −1..1). Удобный размер кадра — 20–50 мс.</summary>
    public void Feed(float[] samples, int sampleRate)
    {
        if (Mode == ListenMode.Off || samples.Length == 0) return;
        _rate = sampleRate;
        var dur = samples.Length * 1000.0 / sampleRate;
        var level = WavEncoder.Rms(samples);
        Level?.Invoke(Math.Min(1f, level / 0.15f));
        if (Paused && Mode == ListenMode.Wake) { _pre.Clear(); _rec = null; return; }
        var thr = Threshold;

        if (_rec is null)
        {
            _pre.Add(samples);
            while (_pre.Count * dur > 450) _pre.RemoveAt(0);
            if (level > thr)
            {
                _loudMs += dur;
                if (_loudMs >= 110)
                {
                    _rec = new List<float[]>(_pre);
                    _pre.Clear();
                    _silentMs = 0;
                    _recMs = 0;
                    if (Mode == ListenMode.Command) StateChanged?.Invoke(RecorderState.Recording, null);
                }
            }
            else
            {
                _loudMs = 0;
                _noise = _noise * 0.97 + level * 0.03;
            }
            if (Mode == ListenMode.Command)
            {
                _sinceCommandMs += dur;
                if (_sinceCommandMs > 7000) Cancel("timeout");
            }
            return;
        }

        _rec.Add(samples);
        _recMs += dur;
        if (level > thr * 0.7) _silentMs = 0; else _silentMs += dur;
        var maxMs = Mode == ListenMode.Command ? 30000 : 9000;
        if (_silentMs >= _silenceMs() || _recMs >= maxMs) Finish(sampleRate);
    }

    private void Finish(int sampleRate)
    {
        var chunks = _rec ?? new List<float[]>();
        var mode = Mode;
        _rec = null;
        _loudMs = 0;
        var total = chunks.Sum(c => c.Length);
        var all = new float[total];
        var o = 0;
        foreach (var c in chunks) { c.CopyTo(all, o); o += c.Length; }
        var durationMs = total * 1000.0 / (_rate > 0 ? _rate : sampleRate);
        if (mode == ListenMode.Command)
        {
            Mode = WakeEnabled ? ListenMode.Wake : ListenMode.Off;
            StateChanged?.Invoke(RecorderState.Processing, null);
        }
        if (durationMs < 350)
        {
            if (mode == ListenMode.Command) StateChanged?.Invoke(Mode == ListenMode.Wake ? RecorderState.Wake : RecorderState.Off, "short");
            return;
        }
        var wav = WavEncoder.Encode(WavEncoder.Downsample(all, _rate, 16000), 16000);
        Segment?.Invoke(wav, mode, durationMs);
    }

    /// <summary>
    /// Проверка фразы в режиме Wake: если в ней есть слово-активатор, возвращает текст команды после него
    /// ("" — только позвали по имени), иначе null.
    /// </summary>
    public static string? MatchWake(string text, string phrases)
    {
        var norm = (text ?? "").ToLowerInvariant().Replace('ё', 'е');
        foreach (var raw in (phrases ?? "вера").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var p = raw.ToLowerInvariant().Replace('ё', 'е');
            var idx = 0;
            while ((idx = norm.IndexOf(p, idx, StringComparison.Ordinal)) >= 0)
            {
                var beforeOk = idx == 0 || !char.IsLetter(norm[idx - 1]);
                var after = idx + p.Length;
                var afterOk = after >= norm.Length || !char.IsLetter(norm[after]);
                if (beforeOk && afterOk)
                    return text![after..].TrimStart(' ', ',', '.', '!', '?', ':', ';', '—', '-').Trim();
                idx = after;
            }
        }
        return null;
    }
}
