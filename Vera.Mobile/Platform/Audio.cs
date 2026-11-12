using Android.Content;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using Android.Speech;
using Android.Speech.Tts;

using Encoding = Android.Media.Encoding;
using TextToSpeech = Android.Speech.Tts.TextToSpeech;
using Locale = Java.Util.Locale;
using Stream = Android.Media.Stream;

namespace Vera.Mobile.Platform;

/// <summary>Микрофон через AudioRecord: 16 кГц моно, кадры по 20 мс (float −1..1).</summary>
public sealed class MicCapture
{
    public const int Rate = 16000;
    private AudioRecord? _rec;
    private Thread? _thread;
    private volatile bool _run;

    public bool Running => _run;

    public void Start(Action<float[], int> onFrame)
    {
        if (_run) return;
        var min = AudioRecord.GetMinBufferSize(Rate, ChannelIn.Mono, Encoding.Pcm16bit);
        if (min <= 0) throw new InvalidOperationException("Микрофон не поддерживает 16 кГц");
        var rec = new AudioRecord(AudioSource.VoiceRecognition, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Rate / 5 * 2));
        if (rec.State != State.Initialized)
        {
            rec.Release();
            rec = new AudioRecord(AudioSource.Mic, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Rate / 5 * 2));
            if (rec.State != State.Initialized)
            {
                rec.Release();
                throw new InvalidOperationException("Не удалось открыть микрофон");
            }
        }
        _rec = rec;
        rec.StartRecording();
        _run = true;
        _thread = new Thread(() =>
        {
            var frame = Rate / 50; // 20 мс
            var buf = new short[frame];
            while (_run)
            {
                int n;
                try { n = rec.Read(buf, 0, frame); }
                catch { break; }
                if (n <= 0) { if (n < 0) Thread.Sleep(20); continue; }
                var f = new float[n];
                for (var i = 0; i < n; i++) f[i] = buf[i] / 32768f;
                try { onFrame(f, Rate); } catch (Exception e) { Android.Util.Log.Warn("Vera", "mic frame: " + e.Message); }
            }
        }) { IsBackground = true, Name = "vera-mic", Priority = System.Threading.ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_run && _rec is null) return;
        _run = false;
        try { _thread?.Join(500); } catch { }
        _thread = null;
        try { _rec?.Stop(); } catch { }
        try { _rec?.Release(); } catch { }
        _rec = null;
    }
}

/// <summary>Распознавание речи средствами Android (SpeechRecognizer). Работает только в UI-потоке.</summary>
public sealed class DeviceRecognizer : Java.Lang.Object, IRecognitionListener
{
    private SpeechRecognizer? _sr;
    private bool _active;

    public event Action<string>? Result;
    /// <summary>Ошибка: "short" / "timeout" (ничего не услышала) или текст ошибки.</summary>
    public event Action<string>? Failed;
    public event Action? SpeechStarted;
    public event Action? SpeechEnded;
    public event Action<float>? Level;

    public bool Active => _active;

    public static bool Available(Context ctx)
    {
        try { return SpeechRecognizer.IsRecognitionAvailable(ctx); } catch { return false; }
    }

    public bool Start(string language)
    {
        var ctx = Android.App.Application.Context;
        if (!Available(ctx)) return false;
        _sr ??= SpeechRecognizer.CreateSpeechRecognizer(ctx);
        if (_sr is null) return false;
        _sr.SetRecognitionListener(this);
        var intent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
        intent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
        var lang = string.IsNullOrEmpty(language) ? "ru-RU" : language switch { "ru" => "ru-RU", "en" => "en-US", "uk" => "uk-UA", _ => language };
        intent.PutExtra(RecognizerIntent.ExtraLanguage, lang);
        intent.PutExtra(RecognizerIntent.ExtraLanguagePreference, lang);
        intent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, false);
        intent.PutExtra(RecognizerIntent.ExtraCallingPackage, ctx.PackageName);
        _active = true;
        _sr.StartListening(intent);
        return true;
    }

    public void Stop()
    {
        try { _sr?.StopListening(); } catch { }
    }

    public void Cancel()
    {
        _active = false;
        try { _sr?.Cancel(); } catch { }
    }

    public void Destroy()
    {
        Cancel();
        try { _sr?.Destroy(); } catch { }
        _sr = null;
    }

    public void OnBeginningOfSpeech() => SpeechStarted?.Invoke();
    public void OnBufferReceived(byte[]? buffer) { }
    public void OnEndOfSpeech() => SpeechEnded?.Invoke();

    public void OnError(SpeechRecognizerError error)
    {
        _active = false;
        var reason = error switch
        {
            SpeechRecognizerError.NoMatch => "short",
            SpeechRecognizerError.SpeechTimeout => "timeout",
            SpeechRecognizerError.Client => "cancel",
            SpeechRecognizerError.InsufficientPermissions => "Нет разрешения на микрофон",
            SpeechRecognizerError.Network or SpeechRecognizerError.NetworkTimeout => "Нет сети для распознавания речи",
            SpeechRecognizerError.RecognizerBusy => "busy",
            SpeechRecognizerError.Audio => "Ошибка записи звука",
            SpeechRecognizerError.Server => "Сервис распознавания недоступен",
            _ => "Ошибка распознавания (" + (int)error + ")",
        };
        Failed?.Invoke(reason);
    }

    public void OnEvent(int eventType, Bundle? @params) { }
    public void OnPartialResults(Bundle? partialResults) { }
    public void OnReadyForSpeech(Bundle? @params) { }

    public void OnResults(Bundle? results)
    {
        _active = false;
        var list = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        var text = list is { Count: > 0 } ? list[0] ?? "" : "";
        if (string.IsNullOrWhiteSpace(text)) Failed?.Invoke("short");
        else Result?.Invoke(text);
    }

    public void OnRmsChanged(float rmsdB) => Level?.Invoke(Math.Clamp((rmsdB + 2f) / 12f, 0f, 1f));
}

/// <summary>Проигрывание mp3 (MediaPlayer) с уровнем громкости для липсинка (Visualizer или приближение).</summary>
public sealed class Mp3Player
{
    private int _n;
    private MediaPlayer? _mp;
    private TaskCompletionSource? _tcs;

    public async Task PlayAsync(byte[] data, float rate, float volume, Action<float> level, CancellationToken ct)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"tts_{Interlocked.Increment(ref _n) % 4}.mp3");
        await File.WriteAllBytesAsync(path, data, ct).ConfigureAwait(false);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer? mp = null;
        Visualizer? vis = null;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            mp = new MediaPlayer();
            mp.SetAudioAttributes(new AudioAttributes.Builder()!
                .SetUsage(AudioUsageKind.Media)!
                .SetContentType(AudioContentType.Speech)!
                .Build()!);
            mp.SetDataSource(path);
            mp.Prepare();
            mp.SetVolume(volume, volume);
            mp.Completion += (_, _) => tcs.TrySetResult();
            mp.Error += (_, e) => { e.Handled = true; tcs.TrySetException(new InvalidOperationException("Ошибка воспроизведения " + e.What)); };
            if (Math.Abs(rate - 1f) > 0.01f)
            {
                try { mp.PlaybackParams = mp.PlaybackParams.SetSpeed(Math.Clamp(rate, 0.5f, 2f)); } catch { }
            }
            _mp = mp;
            _tcs = tcs;
            mp.Start();
            vis = TryVisualizer(mp.AudioSessionId);
        }).ConfigureAwait(false);

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        var started = System.Environment.TickCount64;
        var wave = vis is not null ? new byte[Math.Max(128, vis.CaptureSize)] : null;
        var silentSince = System.Environment.TickCount64;
        var useVis = vis is not null;
        try
        {
            while (!tcs.Task.IsCompleted)
            {
                float lv;
                if (useVis && vis is not null && wave is not null)
                {
                    lv = 0;
                    try
                    {
                        if (vis.GetWaveForm(wave) == (int)VisualizerStatus.Success)
                        {
                            double acc = 0;
                            foreach (var b in wave) { var v = ((b & 0xFF) - 128) / 128.0; acc += v * v; }
                            lv = (float)Math.Min(1, Math.Sqrt(acc / wave.Length) * 4.5);
                        }
                    }
                    catch { useVis = false; }
                    if (lv > 0.02f) silentSince = System.Environment.TickCount64;
                    // некоторые прошивки отдают тишину — переходим на приближение
                    else if (System.Environment.TickCount64 - silentSince > 900 && System.Environment.TickCount64 - started > 900) useVis = false;
                }
                else lv = Approx(System.Environment.TickCount64 - started);
                level(lv);
                await Task.WhenAny(tcs.Task, Task.Delay(50)).ConfigureAwait(false);
            }
            await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            level(0);
            var m = mp; var v = vis;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                try { v?.SetEnabled(false); v?.Release(); } catch { }
                try { if (m is not null && m.IsPlaying) m.Stop(); } catch { }
                try { m?.Release(); } catch { }
            });
            if (ReferenceEquals(_mp, mp)) { _mp = null; _tcs = null; }
        }
    }

    /// <summary>Псевдо-уровень речи: слоги ~6 в секунду.</summary>
    public static float Approx(long ms)
    {
        var t = ms / 1000.0;
        var syl = Math.Abs(Math.Sin(t * Math.PI * 6.2)) * (0.65 + 0.35 * Math.Sin(t * 2.3));
        return (float)Math.Clamp(syl, 0, 1);
    }

    private static Visualizer? TryVisualizer(int session)
    {
        try
        {
            if (Android.App.Application.Context.CheckSelfPermission(Android.Manifest.Permission.RecordAudio) != Android.Content.PM.Permission.Granted)
                return null;
            var v = new Visualizer(session);
            var range = Visualizer.GetCaptureSizeRange();
            v.SetCaptureSize(range is { Length: > 0 } ? Math.Max(range[0], Math.Min(256, range[^1])) : 256);
            v.SetEnabled(true);
            return v;
        }
        catch
        {
            return null;
        }
    }

    public void Stop()
    {
        _tcs?.TrySetCanceled();
        var mp = _mp;
        if (mp is null) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try { if (mp.IsPlaying) mp.Stop(); } catch { }
        });
    }
}

/// <summary>Системный синтез речи Android (TextToSpeech).</summary>
public sealed class SystemVoice : Java.Lang.Object, TextToSpeech.IOnInitListener
{
    private TextToSpeech? _tts;
    private TaskCompletionSource<bool>? _init;
    private readonly Dictionary<string, TaskCompletionSource> _pending = new();

    public Task<bool> EnsureAsync()
    {
        if (_init is not null) return _init.Task;
        _init = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        MainThread.BeginInvokeOnMainThread(() =>
        {
            try
            {
                _tts = new TextToSpeech(Android.App.Application.Context, this);
                _tts.SetOnUtteranceProgressListener(new Progress(this));
            }
            catch (Exception)
            {
                _init.TrySetResult(false);
            }
        });
        return _init.Task;
    }

    public void OnInit(OperationResult status) => _init?.TrySetResult(status == OperationResult.Success);

    /// <summary>Голоса движка (русские первыми).</summary>
    public async Task<IReadOnlyList<(string Name, string Label)>> VoicesAsync()
    {
        if (!await EnsureAsync().ConfigureAwait(false) || _tts is null) return Array.Empty<(string, string)>();
        try
        {
            var voices = _tts.Voices?.ToList() ?? new List<Voice>();
            return voices
                .Where(v => v.Locale is not null && !(v.Features?.Contains(TextToSpeech.Engine.KeyFeatureNotInstalled) ?? false))
                .OrderBy(v => v.Locale!.Language == "ru" ? 0 : 1).ThenBy(v => v.Locale!.ToString()).ThenBy(v => v.Name)
                .Select(v => (v.Name ?? "", $"{v.Locale!.DisplayName} — {v.Name}{(v.IsNetworkConnectionRequired ? " (сеть)" : "")}"))
                .ToList();
        }
        catch { return Array.Empty<(string, string)>(); }
    }

    public async Task SpeakAsync(string text, string voiceName, float rate, float volume, CancellationToken ct)
    {
        if (!await EnsureAsync().ConfigureAwait(false) || _tts is null)
            throw new InvalidOperationException("Системный синтез речи недоступен. Установи голосовой движок в настройках Android → Синтез речи.");
        var id = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pending) _pending[id] = tcs;
        await MainThread.InvokeOnMainThreadAsync(() =>
        {
            Voice? voice = null;
            if (!string.IsNullOrEmpty(voiceName)) voice = _tts.Voices?.FirstOrDefault(v => v.Name == voiceName);
            if (voice is not null) _tts.SetVoice(voice);
            else
            {
                var res = _tts.SetLanguage(new Locale("ru", "RU"));
                if (res is LanguageAvailableResult.MissingData or LanguageAvailableResult.NotSupported)
                    tcs.TrySetException(new InvalidOperationException("Нет русского системного голоса — установи его в настройках Android → Синтез речи."));
            }
            _tts.SetSpeechRate(Math.Clamp(rate, 0.5f, 2f));
            var p = new Bundle();
            p.PutFloat(TextToSpeech.Engine.KeyParamVolume, Math.Clamp(volume, 0f, 1f));
            if (!tcs.Task.IsCompleted && _tts.Speak(text, QueueMode.Add, p, id) != OperationResult.Success)
                tcs.TrySetException(new InvalidOperationException("Системный голос не смог произнести текст"));
        }).ConfigureAwait(false);
        using var reg = ct.Register(() => { Stop(); tcs.TrySetCanceled(); });
        try { await tcs.Task.ConfigureAwait(false); }
        finally { lock (_pending) _pending.Remove(id); }
    }

    public void Stop()
    {
        MainThread.BeginInvokeOnMainThread(() => { try { _tts?.Stop(); } catch { } });
        List<TaskCompletionSource> all;
        lock (_pending) all = _pending.Values.ToList();
        foreach (var t in all) t.TrySetCanceled();
    }

    private void Done(string? id, Exception? error)
    {
        if (id is null) return;
        TaskCompletionSource? t;
        lock (_pending) _pending.TryGetValue(id, out t);
        if (t is null) return;
        if (error is null) t.TrySetResult(); else t.TrySetException(error);
    }

    private sealed class Progress : UtteranceProgressListener
    {
        private readonly SystemVoice _owner;
        public Progress(SystemVoice owner) => _owner = owner;
        public override void OnStart(string? utteranceId) { }
        public override void OnDone(string? utteranceId) => _owner.Done(utteranceId, null);
        [Obsolete]
        public override void OnError(string? utteranceId) => _owner.Done(utteranceId, new InvalidOperationException("Ошибка системного голоса"));
        public override void OnError(string? utteranceId, TextToSpeechError errorCode) => _owner.Done(utteranceId, new InvalidOperationException("Ошибка системного голоса (" + errorCode + ")"));
        public override void OnStop(string? utteranceId, bool interrupted) => _owner.Done(utteranceId, null);
    }
}

/// <summary>Короткие звуки: начало/конец записи, напоминание.</summary>
public static class Sfx
{
    private static ToneGenerator? _tone;

    private static void Tone(Android.Media.Tone tone, int ms)
    {
        try
        {
            _tone ??= new ToneGenerator(Stream.Music, 45);
            _tone.StartTone(tone, ms);
        }
        catch { }
    }

    public static void ListenStart() => Tone(Android.Media.Tone.PropBeep, 90);
    public static void ListenStop() => Tone(Android.Media.Tone.PropAck, 120);
    public static void Error() => Tone(Android.Media.Tone.PropNack, 160);

    public static void Reminder()
    {
        try
        {
            var uri = RingtoneManager.GetDefaultUri(RingtoneType.Notification);
            var r = RingtoneManager.GetRingtone(Android.App.Application.Context, uri);
            r?.Play();
        }
        catch { Tone(Android.Media.Tone.PropBeep2, 300); }
    }
}
