using Vera.Core.Agent;
using Vera.LocalAI.Llm;
using Xunit;

namespace Vera.LocalAI.Tests;

/// <summary>
/// Настоящий llama.cpp на крошечной модели со случайными весами (tools/make_tiny_gguf.py).
/// Запускается, только если задан VERA_TINY_GGUF — в CI модели нет.
/// </summary>
public class LlamaEngineSmokeTests
{
    private static string? Model => Environment.GetEnvironmentVariable("VERA_TINY_GGUF") is { } p && File.Exists(p) ? p : null;

    [Fact]
    public async Task LoadsGeneratesCachesPrefixAndCancels()
    {
        if (Model is not { } model) return;
        using var e = new LlamaEngine(Path.Combine(Path.GetTempPath(), "no-backends"));
        await e.EnsureLoadedAsync(new EngineLoadOptions(model, 2048, 0, "cpu"), null, default);
        Assert.Equal(2048, e.ContextSize);

        var msgs = new List<ChatTurn> { new("system", "Ты Вера. " + string.Concat(Enumerable.Repeat("Правило. ", 50))), new("user", "Привет!") };
        var text = new System.Text.StringBuilder();
        var s1 = await e.GenerateAsync(msgs, new GenerateOptions { MaxTokens = 16 }, t => text.Append(t), default);
        Assert.Equal(0, s1.CachedTokens);
        Assert.True(s1.OutputTokens > 0);

        msgs.Add(new("assistant", text.ToString()));
        msgs.Add(new("user", "Ещё"));
        var s2 = await e.GenerateAsync(msgs, new GenerateOptions { MaxTokens = 4 }, _ => { }, default);
        Assert.True(s2.CachedTokens >= s1.PromptTokens, $"префикс не взят из кэша: {s2.CachedTokens} < {s1.PromptTokens}");

        using var cts = new CancellationTokenSource();
        var n = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            e.GenerateAsync(msgs, new GenerateOptions { MaxTokens = 400 }, _ => { if (++n == 3) cts.Cancel(); }, cts.Token));
        var s3 = await e.GenerateAsync(msgs, new GenerateOptions { MaxTokens = 2 }, _ => { }, default);
        Assert.True(s3.OutputTokens > 0);

        await Assert.ThrowsAsync<PromptTooLongException>(() =>
            e.GenerateAsync(new[] { new ChatTurn("user", string.Concat(Enumerable.Repeat("слово ", 3000))) }, new GenerateOptions(), _ => { }, default));
        e.Unload();
        Assert.Null(e.Loaded);
    }

    [Fact]
    public async Task AgentTurnThroughRealEngine()
    {
        if (Model is not { } model) return;
        var root = Path.Combine(Path.GetTempPath(), "vera-real-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        var core = new Vera.Core.VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
        using var local = new LocalAiService(core, root, "none.onnx", new LlamaEngine(Path.Combine(root, "backends")));
        local.Attach();
        core.Settings.Update(s => { s.Provider = "local"; s.Models["local"] = model; s.Local.ContextSize = 4096; s.Local.Backend = "cpu"; });
        var ended = 0;
        core.Agent.Event += ev => { if (ev is TurnEnded) ended++; };
        await core.Agent.SendAsync("Привет!");
        Assert.Equal(1, ended);
        Assert.Contains("токенов/с", await local.Llm.TestAsync());
        try { Directory.Delete(root, true); } catch { /* временная папка */ }
    }
}
