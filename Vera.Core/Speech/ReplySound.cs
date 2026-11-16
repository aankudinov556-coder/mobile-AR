namespace Vera.Core.Speech;

/// <summary>
/// «Голосок» Веры вместо синтеза речи: короткие тоны, пока печатается ответ.
/// Звук генерируется здесь (одинаково на ПК и телефоне), платформа только проигрывает моно-PCM.
/// </summary>
public static class ReplySound
{
    public const int SampleRate = 44100;

    public static readonly IReadOnlyList<(string Id, string Title, string About)> Styles = new[]
    {
        ("pixel", "Пиксель", "Короткие «пип» из старых игр, смягчены фильтром — не пищат"),
        ("drops", "Капли", "Круглые тоны, будто кто-то стучит по стеклу. Самый мягкий"),
        ("soft", "Шорох", "Тихий шелест бумаги с еле слышным тоном"),
        ("off", "Без звука", "Только текст"),
    };

    public static readonly IReadOnlyList<(string Id, string Title)> Volumes = new[]
    {
        ("low", "Тише"), ("mid", "Средне"), ("high", "Громче"),
    };

    public static float Gain(string? volume) => volume switch { "low" => 0.35f, "high" => 0.9f, _ => 0.6f };

    /// <summary>Тон под характер: начальница ниже, милая выше.</summary>
    public static double PitchFor(string? persona) => persona switch
    {
        "boss" => 0.84,
        "business" => 0.92,
        "tease" => 1.06,
        "tsundere" => 1.12,
        "sweet" => 1.2,
        _ => 1.0,
    };

    /// <summary>Один звук. Пусто для "off" и неизвестного стиля.</summary>
    public static float[] Blip(string? style, double pitch, Random rng, float gain = 0.6f)
    {
        pitch = Math.Clamp(pitch, 0.5, 2.0);
        return style switch
        {
            "pixel" => Pixel(pitch, rng, gain),
            "drops" => Drop(pitch, rng, gain),
            "soft" => Rustle(pitch, rng, gain),
            _ => Array.Empty<float>(),
        };
    }

    private static double Pick(Random rng, double baseHz, int[] steps) => baseHz * Math.Pow(2, steps[rng.Next(steps.Length)] / 12.0);

    /// <summary>Прямоугольная волна через однополюсный ФНЧ, атака 4 мс, затухание ~60 мс.</summary>
    private static float[] Pixel(double pitch, Random rng, float gain)
    {
        var f = Pick(rng, 330 * pitch, new[] { 0, 2, 4, 7, 9 });
        var n = (int)(0.07 * SampleRate);
        var buf = new float[n];
        var a = 1 - Math.Exp(-2 * Math.PI * 1700 / SampleRate); // ФНЧ 1,7 кГц срезает «писк»
        double y = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var sq = Math.Sin(2 * Math.PI * f * t) >= 0 ? 1.0 : -1.0;
            y += a * (sq - y);
            buf[i] = (float)(y * Env(t, 0.004, 0.06) * 0.07 * gain / 0.6 * 1.6);
        }
        return buf;
    }

    /// <summary>Синус с падающим тоном — «капля».</summary>
    private static float[] Drop(double pitch, Random rng, float gain)
    {
        var f = Pick(rng, 620 * pitch, new[] { 0, 3, 5, 7, 10 });
        var n = (int)(0.1 * SampleRate);
        var buf = new float[n];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var k = Math.Min(1, t / 0.07);
            var freq = f * 1.25 * Math.Pow(0.8 / 1.25, k);
            phase += 2 * Math.PI * freq / SampleRate;
            buf[i] = (float)(Math.Sin(phase) * Env(t, 0.003, 0.09) * 0.16 * gain / 0.6 * 1.2);
        }
        return buf;
    }

    /// <summary>Шум через полосовой фильтр + тихий треугольный тон.</summary>
    private static float[] Rustle(double pitch, Random rng, float gain)
    {
        var n = (int)(0.06 * SampleRate);
        var buf = new float[n];
        // полосовой фильтр (biquad, RBJ), центр 1,4–1,9 кГц
        var fc = 1400 + rng.NextDouble() * 500;
        var w0 = 2 * Math.PI * fc / SampleRate;
        var alpha = Math.Sin(w0) / (2 * 1.4);
        double b0 = alpha, b2 = -alpha, a0 = 1 + alpha, a1 = -2 * Math.Cos(w0), a2 = 1 - alpha;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        var tone = (260 + rng.NextDouble() * 40) * pitch;
        var noiseLen = (int)(0.05 * SampleRate);
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)SampleRate;
            var x = i < noiseLen ? (rng.NextDouble() * 2 - 1) * (1 - i / (double)noiseLen) * Math.Min(1, t / 0.003) : 0;
            var y = (b0 * x + b2 * x2 - a1 * y1 - a2 * y2) / a0;
            x2 = x1; x1 = x; y2 = y1; y1 = y;
            var tri = 2 / Math.PI * Math.Asin(Math.Sin(2 * Math.PI * tone * t));
            buf[i] = (float)((y * 0.22 * 1.3 + tri * Env(t, 0.005, 0.05) * 0.05) * gain / 0.6);
        }
        return buf;
    }

    /// <summary>Огибающая: линейная атака, экспоненциальное затухание до −60 дБ к концу.</summary>
    private static double Env(double t, double attack, double end)
    {
        if (t < attack) return t / attack;
        if (t >= end) return 0;
        return Math.Pow(0.001, (t - attack) / (end - attack));
    }
}

/// <summary>
/// Темп «голоска»: текст ответа приходит кусками, а звук идёт ровно — один «пип» на две буквы,
/// не чаще раза в ~45 мс и не дольше ~1,2 с вдогонку, если текст пришёл сразу большим куском.
/// Не потокобезопасен: Push/Tick вызывать из одного потока (UI).
/// </summary>
public sealed class BlipPacer
{
    public static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(45);
    public const int MaxPending = 26;

    private int _pending;
    private int _letters;
    private DateTimeOffset _next;

    /// <summary>Сколько звуков ещё впереди.</summary>
    public int Pending => _pending;
    public bool Active => _pending > 0;

    public void Push(string? delta)
    {
        if (string.IsNullOrEmpty(delta)) return;
        foreach (var ch in delta)
        {
            if (!char.IsLetterOrDigit(ch)) continue;
            if (++_letters % 2 == 0) _pending = Math.Min(MaxPending, _pending + 1);
        }
    }

    /// <summary>true — пора проиграть следующий звук.</summary>
    public bool Tick(DateTimeOffset now)
    {
        if (_pending <= 0 || now < _next) return false;
        _pending--;
        _next = now + Interval;
        return true;
    }

    public void Reset()
    {
        _pending = 0;
        _letters = 0;
        _next = default;
    }
}
