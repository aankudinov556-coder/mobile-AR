using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Providers;
using Vera.Core.Speech;
using Vera.LocalAI;
using Vera.LocalAI.Llm;
using Vera.LocalAI.Voice;
using Xunit;

namespace Vera.LocalAI.Tests;

public sealed class MemStorage : IVeraStorage
{
    public readonly Dictionary<string, string> Files = new();
    public string? ReadText(string name) { lock (Files) return Files.TryGetValue(name, out var v) ? v : null; }
    public void WriteText(string name, string content) { lock (Files) Files[name] = content; }
}

public sealed class MemSecrets : ISecretStore
{
    public readonly Dictionary<string, string> Values = new();
    public string? Get(string name) => Values.TryGetValue(name, out var v) ? v : null;
    public void Set(string name, string? value) { if (string.IsNullOrEmpty(value)) Values.Remove(name); else Values[name] = value; }
}

/// <summary>Подделка движка: отдаёт заготовленные ответы кусочками по 3 символа.</summary>
public sealed class FakeEngine : ILocalEngine
{
    public readonly Queue<string> Replies = new();
    public readonly List<IReadOnlyList<ChatTurn>> Prompts = new();
    public Exception? Throw { get; set; }
    public int CharsPerToken { get; set; } = 4;
    public int Context { get; set; } = 8192;
    public int Loads { get; private set; }

    public EngineLoadOptions? Loaded { get; private set; }
    public int ContextSize => Context;
    public string DeviceName => "тест";

    public Task EnsureLoadedAsync(EngineLoadOptions options, IProgress<float>? progress, CancellationToken ct)
    {
        if (Loaded != options) { Loaded = options; Loads++; progress?.Report(1f); }
        return Task.CompletedTask;
    }

    public int CountTokens(IReadOnlyList<ChatTurn> messages) => messages.Sum(m => m.Content.Length) / CharsPerToken + messages.Count * 4;

    public Task<GenerateStats> GenerateAsync(IReadOnlyList<ChatTurn> messages, GenerateOptions options, Action<string> onText, CancellationToken ct)
    {
        if (Throw is not null) throw Throw;
        Prompts.Add(messages.ToList());
        var reply = Replies.Count > 0 ? Replies.Dequeue() : "Ок.";
        var stops = new StopMatcher(options.Stops);
        for (var i = 0; i < reply.Length; i += 3)
        {
            var (emit, stop) = stops.Push(reply.Substring(i, Math.Min(3, reply.Length - i)));
            if (emit.Length > 0) onText(emit);
            if (stop) break;
        }
        var rest = stops.Flush();
        if (rest.Length > 0) onText(rest);
        return Task.FromResult(new GenerateStats(CountTokens(messages), 0, reply.Length / 3, false));
    }

    public void Unload() => Loaded = null;
    public void Dispose() { }
}

public class StreamFilterTests
{
    private static string Run(string text, int chunk)
    {
        var f = new LocalStreamFilter();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length; i += chunk) sb.Append(f.Push(text.Substring(i, Math.Min(chunk, text.Length - i))));
        return sb.Append(f.Flush()).ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(100)]
    public void HidesThinkingAndToolCalls(int chunk)
    {
        var s = "<think>хм, надо напомнить</think>Сейчас поставлю. <tool_call>\n{\"name\":\"x\"}\n</tool_call> Готово <3";
        Assert.Equal("Сейчас поставлю.  Готово <3", Run(s, chunk));
    }

    [Fact]
    public void UnclosedToolCallIsDropped() => Assert.Equal("Текст ", Run("Текст <tool_call>{\"name\":", 4));

    [Fact]
    public void LessThanSignIsKept() => Assert.Equal("a < b и <b>жирный</b>", Run("a < b и <b>жирный</b>", 1));

    [Fact]
    public void StopMatcherCutsAcrossPieces()
    {
        var m = new StopMatcher(new[] { "<tool_response>" });
        var sb = new StringBuilder();
        foreach (var p in new[] { "ответ <to", "ol_resp", "onse> лишнее" })
        {
            var (e, stop) = m.Push(p);
            sb.Append(e);
            if (stop) break;
        }
        Assert.Equal("ответ ", sb.ToString());
    }

    [Fact]
    public void ChatMlFallbackEndsWithAssistant()
    {
        var t = LlamaEngine.ChatMl(new[] { new ChatTurn("system", "s"), new ChatTurn("user", "u") });
        Assert.Equal("<|im_start|>system\ns<|im_end|>\n<|im_start|>user\nu<|im_end|>\n<|im_start|>assistant\n", t);
    }
}

public class LocalProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vera-local-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _model;

    public LocalProviderTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "llm"));
        _model = Path.Combine(_root, "llm", "test-model.gguf");
        File.WriteAllText(_model, "fake");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* временная папка */ }
    }

    private (VeraCore Core, FakeEngine Engine, LocalAiService Local) Make(Action<VeraSettings>? cfg = null)
    {
        var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
        var engine = new FakeEngine();
        var local = new LocalAiService(core, _root, Path.Combine(_root, "none.onnx"), engine);
        local.Attach();
        core.Settings.Update(s =>
        {
            s.Provider = "local";
            s.Models["local"] = _model;
            s.Search.Enabled = false;
            cfg?.Invoke(s);
        });
        return (core, engine, local);
    }

    [Fact]
    public async Task FullTurnCreatesReminderThroughToolCall()
    {
        var (core, engine, local) = Make();
        engine.Replies.Enqueue("Сейчас поставлю.\n<tool_call>\n{\"name\": \"create_reminder\", \"arguments\": {\"text\": \"Выпить воды\", \"in_minutes\": 5}}\n</tool_call>");
        engine.Replies.Enqueue("[smug] Готово, не забудь только.");
        var events = new List<AgentEvent>();
        core.Agent.Event += e => { lock (events) events.Add(e); };

        await core.Agent.SendAsync("Напомни выпить воды через 5 минут");

        var r = Assert.Single(core.Reminders.List());
        Assert.Equal("Выпить воды", r.Text);
        var streamed = string.Concat(events.OfType<TextDelta>().Select(d => d.Delta));
        Assert.DoesNotContain("tool_call", streamed);
        Assert.DoesNotContain("{", streamed);
        Assert.Contains("Готово", streamed);
        Assert.Equal("smug", events.OfType<TurnEnded>().Single().Emotion);

        // первый промпт: инструменты описаны в системном сообщении
        Assert.Equal(2, engine.Prompts.Count);
        Assert.Equal("system", engine.Prompts[0][0].Role);
        Assert.Contains("create_reminder", engine.Prompts[0][0].Content);
        Assert.Contains("<tool_call>", engine.Prompts[0][0].Content);
        // второй: свой вызов + результат инструмента сообщением пользователя
        var p2 = engine.Prompts[1];
        Assert.Equal("assistant", p2[^2].Role);
        Assert.Contains("<tool_call>", p2[^2].Content);
        Assert.Equal("user", p2[^1].Role);
        Assert.Contains("<tool_response name=\"create_reminder\"", p2[^1].Content);
        Assert.Equal(1, engine.Loads);
        local.Dispose();
    }

    [Fact]
    public async Task BareJsonIsHandledByAgentFallback()
    {
        var (core, engine, _) = Make();
        engine.Replies.Enqueue("{\"name\": \"create_reminder\", \"arguments\": {\"text\": \"Позвонить маме\", \"in_minutes\": 30}}");
        engine.Replies.Enqueue("Поставила.");
        await core.Agent.SendAsync("напомни позвонить маме через полчаса");
        Assert.Equal("Позвонить маме", Assert.Single(core.Reminders.List()).Text);
    }

    [Fact]
    public async Task NoModelGivesFriendlyError()
    {
        var (core, _, _) = Make(s => s.Models["local"] = "");
        File.Delete(_model);
        var failed = new List<TurnFailed>();
        core.Agent.Event += e => { if (e is TurnFailed f) failed.Add(f); };
        await Assert.ThrowsAsync<ProviderException>(() => core.Agent.SendAsync("привет"));
        Assert.Contains("Встроенная модель не выбрана", Assert.Single(failed).Message);
    }

    [Fact]
    public async Task FallsBackToCloudOnError()
    {
        var (core, engine, _) = Make(s => { s.Local.CloudFallback = true; s.Local.FallbackProvider = "openrouter"; });
        engine.Throw = new InvalidOperationException("не хватило памяти");
        var cloud = new ScriptedProvider("openrouter", "Ответ из облака");
        core.Providers.Register(cloud);
        core.Secrets.Set("openrouter", "key");
        var ended = new List<TurnEnded>();
        core.Agent.Event += e => { if (e is TurnEnded t) ended.Add(t); };
        await core.Agent.SendAsync("привет");
        Assert.Equal("Ответ из облака", Assert.Single(ended).Text);
        Assert.Equal(1, cloud.Calls);
    }

    [Fact]
    public void ToTurnsMergesRolesAndRendersTools()
    {
        var args = new JsonObject { ["app"] = "notepad" };
        var history = new List<VeraMessage>
        {
            new() { Role = "assistant", Text = "висящий ответ без вопроса" },
            new() { Role = "user", Text = "открой блокнот", Ctx = "сейчас 10:00", Images = new() { new ImageData("image/png", "AAAA") } },
            new() { Role = "assistant", Text = "", ToolCalls = new() { new ToolCall { Id = "1", Name = "open_app", Args = args } } },
            new() { Role = "tool", ToolCallId = "1", Name = "open_app", Text = "Запущено" },
            new() { Role = "assistant", Text = "Открыла." },
            new() { Role = "user", Text = "спасибо", Documents = new() { new DocumentText("d1", "a.txt", "текст файла") } },
        };
        var t = LocalLlmProvider.ToTurns(history);
        Assert.Equal(new[] { "user", "assistant", "user", "assistant", "user" }, t.Select(x => x.Role));
        Assert.Contains("<context>сейчас 10:00</context>", t[0].Content);
        Assert.Contains("не видит картинки", t[0].Content);
        Assert.Contains("\"name\": \"open_app\"", t[1].Content);
        Assert.Contains("\"app\":\"notepad\"", t[1].Content);
        Assert.Contains("Запущено", t[2].Content);
        Assert.Contains("<document id=\"d1\" name=\"a.txt\">", t[4].Content);
    }

    [Fact]
    public async Task LongHistoryIsTrimmedToContext()
    {
        var (core, engine, _) = Make();
        engine.Context = 8192;
        for (var i = 0; i < 6; i++)
        {
            engine.Replies.Enqueue(new string('о', 4000));
            await core.Agent.SendAsync("вопрос " + i + new string('ы', 4000));
        }
        var last = engine.Prompts[^1];
        Assert.True(engine.CountTokens(last) <= 8192 - 1024, "история не урезана: " + engine.CountTokens(last));
        Assert.True(last.Count < 12, "старые сообщения должны выпасть");
        Assert.Contains("вопрос 5", last[^1].Content);
        Assert.Equal("system", last[0].Role);
        Assert.Equal("user", last[1].Role);
    }

    [Fact]
    public async Task ListModelsShowsCatalogTitles()
    {
        var (_, _, local) = Make();
        File.WriteAllText(Path.Combine(_root, "llm", "qwen2.5-3b-instruct-q4_k_m.gguf"), "x");
        var models = await local.Llm.ListModelsAsync();
        Assert.Contains(models, m => m.Name == "Qwen2.5 3B — сбалансированная");
        Assert.Contains(models, m => m.Name == "test-model");
    }

    private sealed class ScriptedProvider(string id, string reply) : ILlmProvider
    {
        public int Calls;
        public string Id => id;
        public string Title => "Облако";
        public ProviderConversation Begin(IReadOnlyList<VeraMessage> history) => new C();
        public Task<StepResult> StepAsync(StepRequest request, ProviderConversation conv, Action<string> onText, CancellationToken ct)
        {
            Calls++;
            onText(reply);
            return Task.FromResult(new StepResult { Text = reply });
        }
        public void AppendToolResults(ProviderConversation conv, IReadOnlyList<ToolExecution> results) { }
        public void AppendUserText(ProviderConversation conv, string text, IReadOnlyList<ImageData>? images = null) { }
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>(Array.Empty<ModelInfo>());
        public Task<string> TestAsync(CancellationToken ct = default) => Task.FromResult("ok");
        private sealed class C : ProviderConversation { }
    }
}

public class DownloadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vera-dl-" + Guid.NewGuid().ToString("N")[..8]);

    public DownloadTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* временная папка */ }
    }

    /// <summary>Сервер с поддержкой Range; первый ответ обрывается на половине.</summary>
    private sealed class RangeHandler(byte[] data, bool breakFirst) : HttpMessageHandler
    {
        public int Requests;
        public readonly List<long?> Ranges = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            var from = request.Headers.Range?.Ranges.First().From;
            Ranges.Add(from);
            var start = (int)(from ?? 0);
            var body = data[start..];
            var brk = breakFirst && Requests == 1;
            var resp = new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
            {
                Content = new StreamContent(new BreakingStream(body, brk ? body.Length / 2 : -1)),
            };
            resp.Content.Headers.ContentLength = body.Length;
            if (from is not null) resp.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, data.Length - 1, data.Length);
            return Task.FromResult(resp);
        }
    }

    private sealed class BreakingStream(byte[] data, int breakAt) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (breakAt >= 0 && Position >= breakAt) throw new IOException("обрыв связи");
            if (breakAt >= 0) count = (int)Math.Min(count, breakAt - Position);
            return base.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (breakAt >= 0 && Position >= breakAt) throw new IOException("обрыв связи");
            if (breakAt >= 0) buffer = buffer[..(int)Math.Min(buffer.Length, breakAt - Position)];
            return base.ReadAsync(buffer, ct);
        }
    }

    [Fact]
    public async Task ResumesAfterBrokenConnection()
    {
        var data = new byte[300_000];
        new Random(1).NextBytes(data);
        var handler = new RangeHandler(data, breakFirst: true);
        using var http = new HttpClient(handler);
        var dest = Path.Combine(_dir, "model.bin");

        await Assert.ThrowsAnyAsync<IOException>(() => ModelDownloader.DownloadAsync(http, "http://x/model.bin", dest, null, default));
        Assert.False(File.Exists(dest));
        Assert.True(File.Exists(dest + ".part"));

        await ModelDownloader.DownloadAsync(http, "http://x/model.bin", dest, null, default);
        Assert.Equal(data, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".part"));
        Assert.Null(handler.Ranges[0]);
        Assert.Equal(150_000, handler.Ranges[1]);
    }

    [Fact]
    public async Task InstallExtractsOnlyPrefixAndBlocksTraversal()
    {
        var zipPath = Path.Combine(_dir, "src.zip");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            void Add(string name, string text)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
            Add("vosk-model-small-ru-0.22/am/final.mdl", "am");
            Add("vosk-model-small-ru-0.22/conf/model.conf", "conf");
            Add("vosk-model-small-ru-0.22/../../evil.txt", "evil");
            Add("other/readme.txt", "skip");
        }
        var bytes = File.ReadAllBytes(zipPath);
        using var http = new HttpClient(new RangeHandler(bytes, breakFirst: false));
        var store = new ModelStore(Path.Combine(_dir, "models"), http);
        var vosk = ModelCatalog.Get("vosk-small-ru")!;
        var reports = new List<DownloadProgress>();
        await store.InstallAsync(vosk, new SyncProgress<DownloadProgress>(reports.Add), default);

        Assert.True(store.IsInstalled(vosk));
        var dir = store.PathOf(vosk);
        Assert.Equal("am", File.ReadAllText(Path.Combine(dir, "am", "final.mdl")));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_dir, "models", "evil.txt")));
        Assert.False(Directory.Exists(Path.Combine(dir, "other")));
        Assert.Contains(reports, r => r.Stage == "extract");
        store.Delete(vosk);
        Assert.False(store.IsInstalled(vosk));
    }

    [Fact]
    public async Task BackendGoesIntoRuntimesTree()
    {
        var zipPath = Path.Combine(_dir, "b.nupkg");
        using (var z = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(z.CreateEntry("LLamaSharpRuntimes/win-x64/native/vulkan/llama.dll").Open());
            w.Write("dll");
        }
        using var http = new HttpClient(new RangeHandler(File.ReadAllBytes(zipPath), breakFirst: false));
        var store = new ModelStore(Path.Combine(_dir, "models"), http);
        var vk = ModelCatalog.Get("backend-vulkan")!;
        await store.InstallAsync(vk, null, default);
        Assert.True(File.Exists(Path.Combine(store.BackendsDir, "runtimes", "win-x64", "native", "vulkan", "llama.dll")));
        Assert.True(store.IsInstalled(vk));
        Assert.False(store.IsInstalled(ModelCatalog.Get("backend-cuda12")!));
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T>
    {
        public void Report(T value) => a(value);
    }
}

public class CatalogAndVoiceTests
{
    private static string Root([CallerFilePath] string f = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(f)!, ".."));

    [Fact]
    public void CatalogIsConsistent()
    {
        var all = ModelCatalog.All.ToList();
        Assert.Equal(all.Count, all.Select(a => a.Id).Distinct().Count());
        Assert.All(all, a => Assert.All(a.Files, f => Assert.StartsWith("https://", f.Url)));
        Assert.All(ModelCatalog.Llms, a => Assert.EndsWith(".gguf", a.Files[0].FileName));
        var irina = ModelCatalog.PiperVoiceFor("ru_RU-irina-medium")!;
        Assert.Equal("https://huggingface.co/rhasspy/piper-voices/resolve/v1.0.0/ru/ru_RU/irina/medium/ru_RU-irina-medium.onnx", irina.Files[0].Url);
        Assert.Equal("ru_RU-irina-medium.onnx.json", irina.Files[1].FileName);
        Assert.NotNull(ModelCatalog.WhisperFor("small"));
        Assert.NotNull(ModelCatalog.WhisperFor(""));
    }

    [Fact]
    public void Recommendations()
    {
        Assert.Equal("qwen2.5-1.5b", new HardwareInfo(4, 4, true, false, false, false).RecommendedLlm());
        Assert.Equal("qwen3-4b", new HardwareInfo(12, 8, true, false, true, false).RecommendedLlm());
        Assert.Equal("qwen2.5-7b", new HardwareInfo(32, 16, true, false, true, true).RecommendedLlm());
        Assert.Equal("base", new HardwareInfo(8, 4, false, false, false, false).RecommendedWhisper());
        Assert.NotEmpty(HardwareInfo.Detect().Describe());
    }

    [Fact]
    public void WakeWordHelpers()
    {
        Assert.Equal(new[] { "вера", "верочка", "верунь" }, VoskWakeWord.Words("Вера, верочка, ВЕРУНЬ, эй"));
        Assert.Equal(new[] { "вера" }, VoskWakeWord.Words(""));
        Assert.Equal("вера", VoskWakeWord.Match("[unk] вера", new[] { "вера" }));
        Assert.Null(VoskWakeWord.Match("[unk] [unk]", new[] { "вера" }));
        Assert.Equal("вера", VoskWakeWord.Extract("{\"partial\" : \"вера\"}", "partial"));
        Assert.Equal("", VoskWakeWord.Extract("не json", "text"));
    }

    [Fact]
    public void WhisperDropsHallucinatedCaptions()
    {
        Assert.Equal("", WhisperTranscriber.Clean(" Продолжение следует... "));
        Assert.Equal("", WhisperTranscriber.Clean("[BLANK_AUDIO]"));
        Assert.Equal("Вера, открой браузер", WhisperTranscriber.Clean(" Вера, открой браузер "));
    }

    [Fact]
    public void PiperArgsAndLines()
    {
        Assert.Equal("Привет. Как дела?", PiperTts.OneLine("Привет.\r\nКак дела?\n"));
        var args = PiperTts.Args("v.onnx", 0.8, "out").ToList();
        Assert.Equal("0.80", args[args.IndexOf("--length_scale") + 1]);
        Assert.Contains("--output_dir", args);
    }

    [Fact]
    public void SileroRunsOnBundledModel()
    {
        var path = Path.Combine(Root(), "assets", "models", "silero_vad.onnx");
        Assert.True(File.Exists(path), "silero_vad.onnx не найден в assets/models");
        using var vad = new SileroVad(path);
        Assert.True(vad.IsV5);

        // тишина и ровный гул — не речь
        var silence = new float[480];
        float p = 0;
        for (var i = 0; i < 50; i++) p = vad.Probability(silence, 48000);
        Assert.True(p < 0.2f, "тишина распознана как речь: " + p);

        vad.Reset();
        var hum = new float[320];
        var maxHum = 0f;
        for (var f = 0; f < 60; f++)
        {
            for (var i = 0; i < hum.Length; i++) hum[i] = 0.05f * MathF.Sin(2 * MathF.PI * 50 * (f * hum.Length + i) / 16000f);
            maxHum = Math.Max(maxHum, vad.Probability(hum, 16000));
        }
        Assert.True(maxHum < 0.5f, "гул 50 Гц распознан как речь: " + maxHum);
    }

    [Fact]
    public void SileroDetectsSpeechSample()
    {
        // фраза, синтезированная espeak-ng (tools/samples): проверяем, что Silero слышит речь
        var wav = Path.Combine(Root(), "Vera.LocalAI.Tests", "samples", "speech_ru.wav");
        if (!File.Exists(wav)) return;
        var samples = ReadWav(File.ReadAllBytes(wav), out var rate);
        using var vad = new SileroVad(Path.Combine(Root(), "assets", "models", "silero_vad.onnx"));
        var frame = rate / 50;
        var max = 0f;
        for (var i = 0; i + frame <= samples.Length; i += frame) max = Math.Max(max, vad.Probability(samples[i..(i + frame)], rate));
        Assert.True(max > 0.6f, "речь не распознана: " + max);
    }

    private static float[] ReadWav(byte[] b, out int rate)
    {
        rate = BitConverter.ToInt32(b, 24);
        var bits = BitConverter.ToInt16(b, 34);
        var i = 12;
        while (Encoding.ASCII.GetString(b, i, 4) != "data") i += 8 + BitConverter.ToInt32(b, i + 4);
        var len = BitConverter.ToInt32(b, i + 4);
        var start = i + 8;
        if (bits != 16) throw new NotSupportedException();
        var n = Math.Min(len, b.Length - start) / 2;
        var r = new float[n];
        for (var k = 0; k < n; k++) r[k] = BitConverter.ToInt16(b, start + k * 2) / 32768f;
        return r;
    }
}
