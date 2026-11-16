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

/// <summary>Распознавание речи (OpenAI / Groq; Whisper и распознавание ОС — через платформу). Запись делает платформа.</summary>
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
        var opts = new AudioTranscriptionOptions { Prompt = L10n.T("Вера, напомни мне… Вера, открой…") };
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

}
