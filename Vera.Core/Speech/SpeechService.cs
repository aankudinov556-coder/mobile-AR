using System.ClientModel;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using OpenAI;
using OpenAI.Audio;
using Vera.Core.Providers;

namespace Vera.Core.Speech;

/// <summary>Результат синтеза: готовый звук (mp3) или просьба озвучить системным голосом.</summary>
public sealed class SpeechAudio
{
    public byte[]? Audio { get; init; }
    public string Mime { get; init; } = "audio/mpeg";
    /// <summary>true — озвучить средствами ОС (текст в Text).</summary>
    public bool UseSystemVoice { get; init; }
    public string Text { get; init; } = "";
    /// <summary>Голос выключен или текст пустой.</summary>
    public bool Skip { get; init; }
    /// <summary>Скорость речи уже учтена сервисом — платформе не нужно ускорять/замедлять звук.</summary>
    public bool RateApplied { get; init; }
}

/// <summary>Распознавание (OpenAI / Groq) и синтез речи (OpenAI / ElevenLabs). Запись и воспроизведение делает платформа.</summary>
public sealed partial class SpeechService
{
    private readonly SettingsService _settings;
    private readonly SecretsService _secrets;
    private readonly HttpClient _http;

    public SpeechService(SettingsService settings, SecretsService secrets, HttpClient http)
    {
        _settings = settings;
        _secrets = secrets;
        _http = http;
    }

    /// <summary>Локальное распознавание (Whisper на ПК) — задаёт платформа. Используется при stt.provider == "whisper".</summary>
    public Func<byte[], CancellationToken, Task<string>>? LocalTranscriber { get; set; }

    /// <summary>Локальный синтез (Piper на ПК) — задаёт платформа. Используется при tts.engine == "piper".</summary>
    public Func<string, CancellationToken, Task<SpeechAudio>>? LocalSynthesizer { get; set; }

    /// <summary>Распознать речь. Для stt.provider == "device" бросает исключение — это делает платформа.</summary>
    public async Task<string> TranscribeAsync(byte[] audio, string mime = "audio/wav", CancellationToken ct = default)
    {
        var s = _settings.Current.Stt;
        if (s.Provider == "whisper")
        {
            if (LocalTranscriber is null) throw new InvalidOperationException("Локальный Whisper недоступен на этом устройстве");
            return (await LocalTranscriber(audio, ct).ConfigureAwait(false)).Trim();
        }
        if (s.Provider == "device") throw new InvalidOperationException("Распознавание средствами устройства выполняет платформа");
        var groq = s.Provider == "groq";
        var keyName = groq ? "groq" : "openai";
        var key = _secrets.Get(keyName);
        if (string.IsNullOrWhiteSpace(key))
            throw new ProviderException(groq ? "Для распознавания речи нужен ключ Groq (Настройки → Ключи)." : "Для распознавания речи нужен ключ OpenAI (Настройки → Ключи).");
        var options = new OpenAIClientOptions();
        if (groq) options.Endpoint = new Uri("https://api.groq.com/openai/v1");
        else if (Environment.GetEnvironmentVariable("VERA_OPENAI_BASE_URL") is { Length: > 0 } custom) options.Endpoint = new Uri(custom);
        var client = new AudioClient(groq ? s.GroqModel : s.OpenaiModel, new ApiKeyCredential(key), options);
        var ext = mime.Contains("webm") ? "webm" : mime.Contains("ogg") ? "ogg" : mime.Contains("mp4") || mime.Contains("m4a") ? "m4a" : "wav";
        var opts = new AudioTranscriptionOptions { Prompt = "Вера, напомни мне… Вера, открой…" };
        if (!string.IsNullOrEmpty(s.Language)) opts.Language = s.Language;
        try
        {
            using var stream = new MemoryStream(audio);
            var res = await client.TranscribeAudioAsync(stream, "speech." + ext, opts, ct).ConfigureAwait(false);
            return (res.Value.Text ?? "").Trim();
        }
        catch (ClientResultException e)
        {
            throw ProviderException.From(groq ? "Groq" : "OpenAI", e.Status, e.Message, e);
        }
    }

    /// <summary>Синтез речи по текущим настройкам голоса.</summary>
    public async Task<SpeechAudio> SynthesizeAsync(string text, CancellationToken ct = default)
    {
        var t = _settings.Current.Tts;
        var clean = CleanForSpeech(text);
        if (clean.Length == 0 || t.Engine == "off") return new SpeechAudio { Skip = true };
        if (t.Engine == "system") return new SpeechAudio { UseSystemVoice = true, Text = clean };
        if (t.Engine == "piper")
        {
            if (LocalSynthesizer is null) return new SpeechAudio { UseSystemVoice = true, Text = clean };
            return await LocalSynthesizer(clean, ct).ConfigureAwait(false);
        }
        var rate = Math.Clamp(t.Rate, 0.5, 2.0);
        var rateChanged = Math.Abs(rate - 1.0) > 0.02;

        if (t.Engine == "elevenlabs")
        {
            var key = _secrets.Get("elevenlabs");
            if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Нет ключа ElevenLabs — добавь его в настройках или выбери другой голос.");
            var voiceId = string.IsNullOrWhiteSpace(t.ElevenVoiceId) ? "EXAVITQu4vr4xnSDxMaL" : t.ElevenVoiceId;
            using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128");
            req.Headers.Add("xi-api-key", key);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
            var body = new JsonObject
            {
                ["text"] = clean,
                ["model_id"] = string.IsNullOrWhiteSpace(t.ElevenModel) ? "eleven_multilingual_v2" : t.ElevenModel,
                ["voice_settings"] = new JsonObject
                {
                    ["stability"] = 0.4, ["similarity_boost"] = 0.8, ["style"] = 0.35, ["use_speaker_boost"] = true,
                    ["speed"] = Math.Clamp(rate, 0.7, 1.2),
                },
            };
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new ProviderException($"ElevenLabs: ошибка {(int)res.StatusCode} {(err.Length > 200 ? err[..200] : err)}", (int)res.StatusCode);
            }
            return new SpeechAudio
            {
                Audio = await res.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false), Mime = "audio/mpeg",
                RateApplied = !rateChanged || (rate >= 0.7 && rate <= 1.2),
            };
        }

        // OpenAI TTS
        var okey = _secrets.Get("openai");
        if (string.IsNullOrWhiteSpace(okey)) throw new ProviderException("Для голоса OpenAI нужен ключ OpenAI. Или выбери системный голос в настройках.");
        var options = new OpenAIClientOptions();
        if (Environment.GetEnvironmentVariable("VERA_OPENAI_BASE_URL") is { Length: > 0 } custom) options.Endpoint = new Uri(custom);
        var model = string.IsNullOrWhiteSpace(t.OpenaiModel) ? "gpt-4o-mini-tts" : t.OpenaiModel;
        var client = new AudioClient(model, new ApiKeyCredential(okey), options);
        var so = new SpeechGenerationOptions { ResponseFormat = GeneratedSpeechFormat.Mp3 };
        if (rateChanged) so.SpeedRatio = (float)rate; // скорость делает сам сервис — звук не нужно растягивать
        if (model.Contains("gpt-4o") && !string.IsNullOrWhiteSpace(t.OpenaiInstructions)) so.Instructions = t.OpenaiInstructions;
        try
        {
            var res = await client.GenerateSpeechAsync(clean, new GeneratedSpeechVoice(string.IsNullOrWhiteSpace(t.OpenaiVoice) ? "shimmer" : t.OpenaiVoice), so, ct).ConfigureAwait(false);
            return new SpeechAudio { Audio = res.Value.ToArray(), Mime = "audio/mpeg", RateApplied = true };
        }
        catch (ClientResultException e)
        {
            throw ProviderException.From("OpenAI", e.Status, e.Message, e);
        }
    }

    public async Task<IReadOnlyList<(string Id, string Name)>> ElevenVoicesAsync(CancellationToken ct = default)
    {
        var key = _secrets.Get("elevenlabs");
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Нет ключа ElevenLabs");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.elevenlabs.io/v1/voices");
        req.Headers.Add("xi-api-key", key);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new ProviderException($"ElevenLabs: {(int)res.StatusCode}", (int)res.StatusCode);
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return (j?["voices"]?.AsArray() ?? new JsonArray())
            .Where(v => v is not null)
            .Select(v => (v!["voice_id"]?.GetValue<string>() ?? "", v["name"]?.GetValue<string>() ?? ""))
            .ToList();
    }

    public async Task<string> TestGroqAsync(CancellationToken ct = default)
    {
        var key = _secrets.Get("groq");
        if (string.IsNullOrWhiteSpace(key)) throw new ProviderException("Ключ не задан");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.groq.com/openai/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode) throw new ProviderException($"Groq: ошибка {(int)res.StatusCode}", (int)res.StatusCode);
        return "Ключ работает";
    }

    /// <summary>Убирает разметку, ссылки, код и эмодзи, чтобы голос читал естественно.</summary>
    public static string CleanForSpeech(string? text)
    {
        var s = text ?? "";
        s = CodeBlock().Replace(s, " (код смотри в чате) ");
        s = InlineCode().Replace(s, "$1");
        s = ImageMd().Replace(s, "");
        s = LinkMd().Replace(s, "$1");
        s = Url().Replace(s, "ссылка");
        s = Heading().Replace(s, "");
        s = Bullet().Replace(s, "");
        s = MdChars().Replace(s, "");
        s = Emoji().Replace(s, "");
        s = Spaces().Replace(s, " ");
        return s.Trim();
    }

    [GeneratedRegex(@"```[\s\S]*?```")] private static partial Regex CodeBlock();
    [GeneratedRegex(@"`([^`]+)`")] private static partial Regex InlineCode();
    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")] private static partial Regex ImageMd();
    [GeneratedRegex(@"\[([^\]]+)\]\((https?:[^)]+)\)")] private static partial Regex LinkMd();
    [GeneratedRegex(@"https?://\S+")] private static partial Regex Url();
    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+", RegexOptions.Multiline)] private static partial Regex Heading();
    [GeneratedRegex(@"^\s*[-*•]\s+", RegexOptions.Multiline)] private static partial Regex Bullet();
    [GeneratedRegex(@"[*_~|>#]")] private static partial Regex MdChars();
    [GeneratedRegex(@"(\p{Cs}|[☀-➿]|️|‍)")] private static partial Regex Emoji();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();
}

/// <summary>Режет стрим ответа на предложения для озвучки по мере генерации; блоки кода пропускает.</summary>
public sealed partial class SentenceChunker
{
    private readonly Action<string> _onChunk;
    private readonly StringBuilder _buf = new();
    private bool _inCode;

    public SentenceChunker(Action<string> onChunk) => _onChunk = onChunk;

    public void Push(string delta)
    {
        _buf.Append(delta);
        while (true)
        {
            var s = _buf.ToString();
            var fence = s.IndexOf("```", StringComparison.Ordinal);
            if (_inCode)
            {
                if (fence == -1) return;
                _buf.Remove(0, fence + 3);
                _inCode = false;
                continue;
            }
            var cut = -1;
            foreach (Match m in Boundary().Matches(s))
            {
                var end = m.Index + m.Length;
                if (fence != -1 && end > fence) break;
                if (end >= 40) { cut = end; break; }
            }
            if (cut == -1)
            {
                if (fence != -1)
                {
                    var before = s[..fence];
                    if (before.Trim().Length > 0) _onChunk(before);
                    _onChunk("(код — в чате)");
                    _buf.Remove(0, fence + 3);
                    _inCode = true;
                    continue;
                }
                return;
            }
            var chunk = s[..cut];
            _buf.Remove(0, cut);
            if (chunk.Trim().Length > 0) _onChunk(chunk);
        }
    }

    public void End()
    {
        if (!_inCode && _buf.ToString().Trim().Length > 0) _onChunk(_buf.ToString());
        _buf.Clear();
        _inCode = false;
    }

    [GeneratedRegex(@"([.!?…]+[""»)\]]?\s+|\n+)")] private static partial Regex Boundary();
}
