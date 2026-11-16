using Android.Media;
using Encoding = Android.Media.Encoding;
using Vera.Core;
using Vera.Core.Speech;

namespace Vera.Mobile.Services;

/// <summary>
/// «Голосок» Веры вместо синтеза речи (v2.5): пока печатается ответ, звучат короткие тоны в такт буквам.
/// Один поток AudioTrack на время ответа; звуки смешиваются по 10 мс, темп задаёт общий <see cref="BlipPacer"/>.
/// Методы можно вызывать из любого потока; события — из фонового.
/// </summary>
public sealed class SpeechOutput
{
    private const int Chunk = 441; // 10 мс при 44,1 кГц
    private static readonly TimeSpan Linger = TimeSpan.FromMilliseconds(350);

    private readonly VeraCore _core;
    private readonly object _gate = new();
    private readonly BlipPacer _pacer = new();
    private readonly List<(float[] Data, int Pos)> _voices = new();
    private readonly Random _rng = new();
    private bool _ended = true;
    private bool _running;
    private int _speaking;

    public SpeechOutput(VeraCore core) => _core = core;

    /// <summary>true — «голосок» зазвучал, false — затих (из фонового потока).</summary>
    public event Action<bool>? SpeakingChanged;
    /// <summary>Уровень 0..1 для рта (из фонового потока).</summary>
    public event Action<float>? Level;

    /// <summary>Звучит или ещё будет звучать в этом ответе.</summary>
    public bool IsActive { get { lock (_gate) return _running && (_pacer.Active || _voices.Count > 0 || !_ended); } }

    public bool IsSpeaking => Volatile.Read(ref _speaking) == 1;

    /// <summary>Начался ответ.</summary>
    public void Begin()
    {
        lock (_gate)
        {
            _pacer.Reset();
            _ended = false;
        }
    }

    /// <summary>Кусок текста ответа, как он пришёл в чат.</summary>
    public void Push(string text)
    {
        if (_core.Settings.Current.Sound.Style == "off") return;
        lock (_gate)
        {
            _pacer.Push(text);
            if (!_pacer.Active || _running) return;
            _running = true;
        }
        _ = Task.Run(Loop);
    }

    /// <summary>Ответ закончился — доиграть и замолчать.</summary>
    public void End()
    {
        lock (_gate) _ended = true;
    }

    /// <summary>Короткая фраза целиком: напоминание, тест в настройках.</summary>
    public void Say(string text)
    {
        Stop();
        Begin();
        Push(text);
        End();
    }

    /// <summary>Замолчать сразу.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _pacer.Reset();
            _voices.Clear();
            _ended = true;
        }
    }

    private void Loop()
    {
        AudioTrack? track = null;
        try
        {
            var min = AudioTrack.GetMinBufferSize(ReplySound.SampleRate, ChannelOut.Mono, Encoding.Pcm16bit);
            track = new AudioTrack.Builder()
                .SetAudioAttributes(new AudioAttributes.Builder()!
                    .SetUsage(AudioUsageKind.Assistant)!
                    .SetContentType(AudioContentType.Sonification)!
                    .Build()!)!
                .SetAudioFormat(new AudioFormat.Builder()!
                    .SetSampleRate(ReplySound.SampleRate)!
                    .SetChannelMask(ChannelOut.Mono)!
                    .SetEncoding(Encoding.Pcm16bit)!
                    .Build()!)!
                .SetBufferSizeInBytes(Math.Max(min, Chunk * 2 * 4))!
                .SetTransferMode(AudioTrackMode.Stream)!
                .Build()!;
            track.Play();
            var mix = new float[Chunk];
            var pcm = new short[Chunk];
            var quietSince = DateTimeOffset.UtcNow;
            while (true)
            {
                Array.Clear(mix);
                bool any, busy;
                lock (_gate)
                {
                    var now = DateTimeOffset.UtcNow;
                    if (_pacer.Tick(now))
                    {
                        var s = _core.Settings.Current;
                        var blip = ReplySound.Blip(s.Sound.Style, ReplySound.PitchFor(s.Persona), _rng, ReplySound.Gain(s.Sound.Volume));
                        if (blip.Length > 0) _voices.Add((blip, 0));
                    }
                    for (var v = _voices.Count - 1; v >= 0; v--)
                    {
                        var (data, pos) = _voices[v];
                        var n = Math.Min(Chunk, data.Length - pos);
                        for (var i = 0; i < n; i++) mix[i] += data[pos + i];
                        if (pos + n >= data.Length) _voices.RemoveAt(v);
                        else _voices[v] = (data, pos + n);
                    }
                    any = _voices.Count > 0;
                    busy = any || _pacer.Active;
                    if (busy) quietSince = now;
                    if (!_pacer.Active && !any && (_ended || now - quietSince > TimeSpan.FromSeconds(3)) && now - quietSince > Linger)
                    {
                        _running = false;
                        break;
                    }
                }
                double peak = 0;
                for (var i = 0; i < Chunk; i++)
                {
                    var x = Math.Clamp(mix[i], -1f, 1f);
                    peak = Math.Max(peak, Math.Abs(x));
                    pcm[i] = (short)(x * short.MaxValue);
                }
                SetSpeaking(busy);
                Level?.Invoke(any ? (float)Math.Min(1, peak * 6) : 0f);
                track.Write(pcm, 0, Chunk); // блокирует — задаёт ритм цикла
            }
        }
        catch (Exception)
        {
            lock (_gate)
            {
                _running = false;
                _voices.Clear();
                _pacer.Reset();
            }
        }
        finally
        {
            try { track?.Stop(); } catch { }
            try { track?.Release(); } catch { }
            SetSpeaking(false);
            Level?.Invoke(0);
        }
    }

    private void SetSpeaking(bool value)
    {
        var old = Interlocked.Exchange(ref _speaking, value ? 1 : 0) == 1;
        if (old == value) return;
        try { SpeakingChanged?.Invoke(value); } catch { }
    }
}
