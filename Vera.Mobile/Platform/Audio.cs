using Vera.Core;
using Android.Content;
using Android.Media;
using Android.Media.Audiofx;
using Android.OS;
using Android.Speech;

using Encoding = Android.Media.Encoding;
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
        if (min <= 0) throw new InvalidOperationException(L10n.T("Микрофон не поддерживает 16 кГц"));
        var rec = new AudioRecord(AudioSource.VoiceRecognition, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Rate / 5 * 2));
        if (rec.State != State.Initialized)
        {
            rec.Release();
            rec = new AudioRecord(AudioSource.Mic, Rate, ChannelIn.Mono, Encoding.Pcm16bit, Math.Max(min, Rate / 5 * 2));
            if (rec.State != State.Initialized)
            {
                rec.Release();
                throw new InvalidOperationException(L10n.T("Не удалось открыть микрофон"));
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
    /// <summary>Промежуточный текст, пока человек говорит.</summary>
    public event Action<string>? Partial;
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
        intent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
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
            SpeechRecognizerError.InsufficientPermissions => L10n.T("Нет разрешения на микрофон"),
            SpeechRecognizerError.Network or SpeechRecognizerError.NetworkTimeout => L10n.T("Нет сети для распознавания речи"),
            SpeechRecognizerError.RecognizerBusy => "busy",
            SpeechRecognizerError.Audio => L10n.T("Ошибка записи звука"),
            SpeechRecognizerError.Server => L10n.T("Сервис распознавания недоступен"),
            _ => L10n.T("Ошибка распознавания (") + (int)error + ")",
        };
        Failed?.Invoke(reason);
    }

    public void OnEvent(int eventType, Bundle? @params) { }
    public void OnPartialResults(Bundle? partialResults)
    {
        var list = partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
        var text = list is { Count: > 0 } ? list[0] : null;
        if (!string.IsNullOrWhiteSpace(text)) Partial?.Invoke(text.Trim());
    }
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
