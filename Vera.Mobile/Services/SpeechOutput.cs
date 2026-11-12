using System.Threading.Channels;
using Vera.Core;
using Vera.Core.Speech;
using Vera.Mobile.Platform;

namespace Vera.Mobile.Services;

/// <summary>
/// Очередь озвучки: куски текста синтезируются заранее (до двух параллельно), проигрываются строго по порядку.
/// Облачный голос — mp3 через MediaPlayer, системный — Android TextToSpeech.
/// </summary>
public sealed class SpeechOutput
{
    private sealed record Item(Task<SpeechAudio?> Audio, CancellationToken Ct);

    private readonly VeraCore _core;
    private readonly Mp3Player _player = new();
    private readonly SemaphoreSlim _synth = new(2);
    private readonly Channel<Item> _queue = Channel.CreateUnbounded<Item>(new UnboundedChannelOptions { SingleReader = true });
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();
    private int _outstanding;
    private int _speaking;
    private bool _errorShown;

    public SpeechOutput(VeraCore core)
    {
        _core = core;
        _ = Task.Run(PlayLoop);
    }

    public SystemVoice SystemVoice { get; } = new();

    /// <summary>true — начала говорить, false — закончила (из фонового потока).</summary>
    public event Action<bool>? SpeakingChanged;
    /// <summary>Уровень звука 0..1 для липсинка (из фонового потока).</summary>
    public event Action<float>? Level;
    /// <summary>Ошибка голоса (показывается один раз за «сеанс»).</summary>
    public event Action<string>? Error;

    /// <summary>Есть что говорить (играет или ждёт в очереди).</summary>
    public bool IsActive => Volatile.Read(ref _outstanding) > 0;

    public bool IsSpeaking => Volatile.Read(ref _speaking) == 1;

    public void ResetErrors() => _errorShown = false;

    public void Enqueue(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_core.Settings.Current.Tts.Engine == "off") return;
        CancellationToken ct;
        lock (_gate) ct = _cts.Token;
        Interlocked.Increment(ref _outstanding);
        var task = SynthAsync(text, ct);
        _queue.Writer.TryWrite(new Item(task, ct));
    }

    private async Task<SpeechAudio?> SynthAsync(string text, CancellationToken ct)
    {
        await _synth.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _core.Speech.SynthesizeAsync(text, ct).ConfigureAwait(false);
        }
        finally
        {
            _synth.Release();
        }
    }

    private async Task PlayLoop()
    {
        await foreach (var item in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (item.Ct.IsCancellationRequested) continue;
                SpeechAudio? audio;
                try { audio = await item.Audio.ConfigureAwait(false); }
                catch (OperationCanceledException) { continue; }
                catch (Exception e) { Report(e.Message); continue; }
                if (audio is null || audio.Skip || item.Ct.IsCancellationRequested) continue;

                SetSpeaking(true);
                var t = _core.Settings.Current.Tts;
                if (audio.UseSystemVoice)
                {
                    using var lipCts = CancellationTokenSource.CreateLinkedTokenSource(item.Ct);
                    var lip = Task.Run(async () =>
                    {
                        var start = System.Environment.TickCount64;
                        while (!lipCts.IsCancellationRequested)
                        {
                            Level?.Invoke(Mp3Player.Approx(System.Environment.TickCount64 - start));
                            try { await Task.Delay(50, lipCts.Token).ConfigureAwait(false); } catch { break; }
                        }
                        Level?.Invoke(0);
                    });
                    try { await SystemVoice.SpeakAsync(audio.Text, t.SystemVoice, (float)t.Rate, (float)t.Volume, item.Ct).ConfigureAwait(false); }
                    finally { lipCts.Cancel(); await lip.ConfigureAwait(false); }
                }
                else if (audio.Audio is { Length: > 0 } bytes)
                {
                    await _player.PlayAsync(bytes, (float)t.Rate, (float)Math.Clamp(t.Volume, 0, 1), l => Level?.Invoke(l), item.Ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Report(e.Message); }
            finally
            {
                if (Interlocked.Decrement(ref _outstanding) <= 0)
                {
                    Interlocked.Exchange(ref _outstanding, 0);
                    SetSpeaking(false);
                }
            }
        }
    }

    private void SetSpeaking(bool value)
    {
        var old = Interlocked.Exchange(ref _speaking, value ? 1 : 0) == 1;
        if (old == value) return;
        if (!value) Level?.Invoke(0);
        try { SpeakingChanged?.Invoke(value); } catch { }
    }

    private void Report(string message)
    {
        if (_errorShown) return;
        _errorShown = true;
        try { Error?.Invoke(message); } catch { }
    }

    /// <summary>Замолчать и выбросить очередь.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts.Cancel();
            _cts = new CancellationTokenSource();
        }
        _player.Stop();
        SystemVoice.Stop();
    }
}
