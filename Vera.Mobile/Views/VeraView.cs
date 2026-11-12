using SkiaSharp;
using SkiaSharp.Views.Maui;
using SkiaSharp.Views.Maui.Controls;
using Vera.Core.Sprites;

namespace Vera.Mobile.Views;

/// <summary>Слои спрайта из assets/vera (MauiAsset, без пережатия).</summary>
public static class SpriteAtlas
{
    private static readonly Dictionary<string, SKImage?> Cache = new();

    public static SKImage? Get(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var img)) return img;
            try
            {
                using var s = Android.App.Application.Context.Assets!.Open("vera/" + name + ".png");
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                img = SKImage.FromEncodedData(ms.ToArray());
            }
            catch (Exception e)
            {
                Android.Util.Log.Warn("Vera", "sprite " + name + ": " + e.Message);
                img = null;
            }
            Cache[name] = img;
            return img;
        }
    }

    public static void Preload()
    {
        foreach (var n in VeraSprite.AllLayerNames()) Get(n);
    }
}

/// <summary>
/// Пиксельная чиби-Вера, сидящая на верхнем краю панели ввода. Рисуется целым масштабом без сглаживания.
/// Моргает, дышит (голова опускается на 1 px), болтает ногами, меняет мимику, шевелит ртом.
/// Касания вне непрозрачных пикселей проходят к элементам под ней.
/// </summary>
public sealed class VeraView : SKCanvasView
{
    private const int W = VeraSprite.Width;
    private const int H = VeraSprite.Height;
    private const int HeadSplit = 42; // строки выше — «голова», опускается при выдохе

    private readonly SKBitmap _frame = new(W, H, SKColorType.Rgba8888, SKAlphaType.Premul);
    private readonly Random _rnd = new();
    private string _frameKey = "";
    private IDispatcherTimer? _timer;

    // мимика
    private string _emotion = "neutral";
    private long _emotionUntil;
    private bool _blink;
    private long _nextBlink;
    private long _blinkEnd;
    private bool _doubleBlink;
    // рот
    private volatile float _level;
    private long _flapUntil;
    // ноги
    private int _legFrame;
    private long _nextSwing;
    private long _swingUntil;
    private long _nextLegStep;
    private int _legStep;
    // дыхание
    private bool _exhale;
    private long _nextBreath;
    // касания
    private long _downAt;
    private SKPoint _downPos;
    private bool _pressed;
    private bool _longFired;

    public VeraView(double unitDp)
    {
        Unit = unitDp;
        var density = DeviceDisplay.MainDisplayInfo.Density;
        if (density <= 0) density = 2.75;
        PxScale = Math.Max(1, (int)Math.Round(unitDp * density));
        // запас в пару физических пикселей: округление раскладки не должно уменьшить целый масштаб
        var slack = 2 / density;
        WidthRequest = W * unitDp + slack;
        HeightRequest = H * unitDp + slack;
        HorizontalOptions = LayoutOptions.Start;
        VerticalOptions = LayoutOptions.Start;
        IgnorePixelScaling = false;
        EnableTouchEvents = true;
        BackgroundColor = Colors.Transparent;
        PaintSurface += OnPaint;
        Touch += OnTouch;
        Loaded += (_, _) => StartTimer();
        Unloaded += (_, _) => { _timer?.Stop(); _timer = null; };
        var now = Now;
        _nextBlink = now + 1500;
        _nextSwing = now + 1200;
        _nextBreath = now + 1400;
    }

    /// <summary>dp на один пиксель спрайта.</summary>
    public double Unit { get; }

    /// <summary>Физических пикселей экрана на один пиксель спрайта (целое — без сглаживания).</summary>
    public int PxScale { get; }

    public PetMode Mode { get; set; } = PetMode.Idle;

    /// <summary>Сейчас говорит голосом (рот по уровню звука).</summary>
    public bool Speaking { get; set; }

    public string CurrentEmotion => Now < _emotionUntil ? _emotion : "neutral";

    public event Action? Tapped;
    public event Action? LongPressed;

    private static long Now => System.Environment.TickCount64;

    /// <summary>Подобрать масштаб: целое число физических пикселей на пиксель спрайта, около targetDp на пиксель.</summary>
    public static double PickUnit(double density, double targetDp = 1.8)
    {
        if (density <= 0) density = 2.75;
        var px = Math.Max(2, (int)Math.Round(targetDp * density));
        return px / density;
    }

    public void SetEmotion(string emotion, int ms = 9000)
    {
        if (!VeraSprite.Emotions.Contains(emotion)) return;
        _emotion = emotion;
        _emotionUntil = Now + ms;
        if (emotion is "happy" or "laugh") _nextSwing = Now; // радостно болтает ногами
    }

    /// <summary>Уровень голоса 0..1 (липсинк), можно звать из любого потока.</summary>
    public void SetLevel(float level) => _level = level;

    /// <summary>Текст идёт без голоса — рот «шевелится» ещё 260 мс.</summary>
    public void Flap() => _flapUntil = Now + 260;

    public async void Hop()
    {
        try
        {
            this.CancelAnimations();
            await this.TranslateToAsync(0, -9 * Unit / 1.8, 130, Easing.CubicOut);
            await this.TranslateToAsync(0, 0, 220, Easing.BounceOut);
        }
        catch { TranslationY = 0; }
    }

    public async void Shake()
    {
        try
        {
            this.CancelAnimations();
            for (var i = 0; i < 4; i++)
            {
                await this.TranslateToAsync(i % 2 == 0 ? -3 : 3, 0, 55);
            }
            await this.TranslateToAsync(0, 0, 55);
        }
        catch { TranslationX = 0; }
    }

    private void StartTimer()
    {
        if (_timer is not null) return;
        SpriteAtlas.Preload();
        _timer = Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick += (_, _) => Step();
        _timer.Start();
        InvalidateSurface();
    }

    private void Step()
    {
        var now = Now;

        // моргание: каждые 2.2–6.4 с на 120 мс, иногда дважды
        if (!_blink && now >= _nextBlink)
        {
            _blink = true;
            _blinkEnd = now + 120;
        }
        else if (_blink && now >= _blinkEnd)
        {
            _blink = false;
            if (!_doubleBlink && _rnd.NextDouble() < 0.18) { _doubleBlink = true; _nextBlink = now + 160; }
            else { _doubleBlink = false; _nextBlink = now + 2200 + _rnd.Next(4200); }
        }

        // дыхание: вдох длиннее выдоха, во сне медленнее
        if (now >= _nextBreath)
        {
            _exhale = !_exhale;
            var slow = Mode == PetMode.Sleeping ? 1.6 : 1.0;
            _nextBreath = now + (long)((_exhale ? 1100 : 1700) * slow);
        }

        // ноги: периодически болтает (0 → 1 → 0 → 2)
        var swingAllowed = Mode is PetMode.Idle or PetMode.Talking or PetMode.Thinking;
        if (!swingAllowed) _swingUntil = 0;
        else if (now >= _nextSwing && now > _swingUntil)
        {
            var happy = CurrentEmotion is "happy" or "laugh" or "smug";
            _swingUntil = now + 1400 + _rnd.Next(happy ? 3000 : 1800);
            _nextSwing = _swingUntil + (happy ? 1500 : 3000) + _rnd.Next(happy ? 3000 : 6000);
            _legStep = 0;
            _nextLegStep = now;
        }
        if (now < _swingUntil)
        {
            if (now >= _nextLegStep)
            {
                _legStep = (_legStep + 1) % 4;
                _legFrame = _legStep switch { 1 => 1, 3 => 2, _ => 0 };
                _nextLegStep = now + (Mode == PetMode.Thinking ? 280 : 170);
            }
        }
        else _legFrame = 0;

        // долгий тап
        if (_pressed && !_longFired && now - _downAt > 520)
        {
            _longFired = true;
            try { HapticFeedback.Default.Perform(HapticFeedbackType.LongPress); } catch { }
            LongPressed?.Invoke();
        }

        // рот
        var level = _level;
        var flapping = now < _flapUntil;
        var talking = Mode == PetMode.Talking || Speaking || flapping;
        var mouthOpen = false;
        if (Speaking && level > 0.18f) mouthOpen = level > 0.35f || ((now >> 7) & 1) == 0;
        else if (flapping) mouthOpen = ((now >> 7) & 1) == 0;
        var mode = Mode;
        if (talking && (Mode == PetMode.Idle || Mode == PetMode.Talking)) mode = PetMode.Talking;

        var frame = VeraSprite.Compose(CurrentEmotion, mode, _blink, mouthOpen, dragging: false, sitting: true, legFrame: _legFrame);
        var key = frame.Key + (_exhale ? "|x" : "|i");
        if (key == _frameKey) return;
        _frameKey = key;
        ComposeFrame(frame.Layers, _exhale);
        InvalidateSurface();
    }

    private void ComposeFrame(IReadOnlyList<string> layers, bool exhale)
    {
        using var c = new SKCanvas(_frame);
        c.Clear(SKColors.Transparent);
        foreach (var name in layers)
        {
            var img = SpriteAtlas.Get(name);
            if (img is null) continue;
            if (name.StartsWith("legs_", StringComparison.Ordinal) || !exhale)
            {
                c.DrawImage(img, 0, 0);
            }
            else if (name.StartsWith("sit_", StringComparison.Ordinal))
            {
                // тело: низ на месте, голова на пиксель ниже (выдох)
                c.DrawImage(img, SKRect.Create(0, HeadSplit, W, H - HeadSplit), SKRect.Create(0, HeadSplit, W, H - HeadSplit));
                c.DrawImage(img, SKRect.Create(0, 0, W, HeadSplit), SKRect.Create(0, 1, W, HeadSplit));
            }
            else
            {
                c.DrawImage(img, 0, 1);
            }
        }
        c.Flush();
    }

    private (float X0, float Y0, int K) Geometry(SKImageInfo info)
    {
        var k = Math.Max(1, Math.Min(PxScale, Math.Min(info.Width / W, info.Height / H)));
        return (0, 0, k);
    }

    private void OnPaint(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        if (_frameKey.Length == 0) Step();
        var (x0, y0, k) = Geometry(e.Info);
        using var img = SKImage.FromBitmap(_frame);
        canvas.DrawImage(img, SKRect.Create(x0, y0, W * k, H * k), new SKSamplingOptions(SKFilterMode.Nearest, SKMipmapMode.None), null);
    }

    private bool HitSprite(SKPoint p)
    {
        var info = CanvasSize;
        var k = Math.Max(1, Math.Min(PxScale, Math.Min((int)info.Width / W, (int)info.Height / H)));
        var px = (int)Math.Floor(p.X / k);
        var py = (int)Math.Floor(p.Y / k);
        for (var dy = -1; dy <= 1; dy++)
        for (var dx = -1; dx <= 1; dx++)
        {
            int x = px + dx, y = py + dy;
            if (x < 0 || y < 0 || x >= W || y >= H) continue;
            if (_frame.GetPixel(x, y).Alpha > 20) return true;
        }
        return false;
    }

    private void OnTouch(object? sender, SKTouchEventArgs e)
    {
        switch (e.ActionType)
        {
            case SKTouchAction.Pressed:
                if (!HitSprite(e.Location)) { e.Handled = false; return; }
                _pressed = true;
                _longFired = false;
                _downAt = Now;
                _downPos = e.Location;
                e.Handled = true;
                return;
            case SKTouchAction.Moved:
                if (_pressed && SKPoint.Distance(_downPos, e.Location) > 24 * DeviceDisplay.MainDisplayInfo.Density / 2.75) _pressed = false;
                e.Handled = true;
                return;
            case SKTouchAction.Released:
                if (_pressed && !_longFired) Tapped?.Invoke();
                _pressed = false;
                e.Handled = true;
                return;
            case SKTouchAction.Cancelled:
            case SKTouchAction.Exited:
                _pressed = false;
                e.Handled = true;
                return;
            default:
                e.Handled = _pressed;
                return;
        }
    }
}
