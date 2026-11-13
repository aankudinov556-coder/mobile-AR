using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Vera.Core;
using Vera.Core.Agent;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Vera.Core.Tests;

/// <summary>Мини-сервер, отвечающий SSE-потоками как OpenAI / Anthropic.</summary>
internal sealed class MockServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<string, JsonObject, IEnumerable<string>> _handler;
    public readonly List<(string Path, JsonObject Body, string? Beta)> Requests = new();

    public MockServer(Func<string, JsonObject, IEnumerable<string>> handler)
    {
        _handler = handler;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(Loop);
    }

    public int Port { get; }
    public string Base => $"http://127.0.0.1:{Port}";

    private async Task Loop()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            using var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            var json = string.IsNullOrWhiteSpace(body) ? new JsonObject() : (JsonNode.Parse(body) as JsonObject ?? new JsonObject());
            lock (Requests) Requests.Add((ctx.Request.Url!.AbsolutePath, json, ctx.Request.Headers["anthropic-beta"]));
            var chunks = _handler(ctx.Request.Url.AbsolutePath, json).ToList();
            if (chunks.Count > 0 && chunks[0].StartsWith("STATUS:"))
            {
                // ошибка: STATUS:404 + тело JSON
                ctx.Response.StatusCode = int.Parse(chunks[0][7..]);
                ctx.Response.ContentType = "application/json";
                var err = Encoding.UTF8.GetBytes(string.Concat(chunks.Skip(1)));
                await ctx.Response.OutputStream.WriteAsync(err);
                ctx.Response.Close();
                continue;
            }
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.SendChunked = true;
            var output = ctx.Response.OutputStream;
            foreach (var chunk in chunks)
            {
                var bytes = Encoding.UTF8.GetBytes(chunk);
                await output.WriteAsync(bytes);
                await output.FlushAsync();
            }
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { /* ignore */ }
    }
}

public class AgentIntegrationTests
{
    private static (VeraCore Core, List<AgentEvent> Events) NewCore(string provider, string model, string keyName)
    {
        var secrets = new MemSecrets();
        secrets.Set(keyName, "sk-test");
        var core = new VeraCore(new MemStorage(), secrets, null, "desktop");
        core.Settings.Update(s => { s.Provider = provider; s.Models[provider] = model; });
        var events = new List<AgentEvent>();
        core.Agent.Event += e => { lock (events) events.Add(e); };
        return (core, events);
    }

    // ------------------------------------------------------------------ OpenAI
    private static string OpenAiChunk(JsonObject delta, string? finish = null) =>
        "data: " + new JsonObject
        {
            ["id"] = "x", ["object"] = "chat.completion.chunk", ["created"] = 1, ["model"] = "mock",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        }.ToJsonString() + "\n\n";

    [Fact]
    public async Task OpenAiToolCallCreatesReminderAndStreamsText()
    {
        using var server = new MockServer((path, body) =>
        {
            var msgs = body["messages"]!.AsArray();
            var last = msgs[^1]!;
            if (last["role"]!.GetValue<string>() == "tool")
            {
                return new[]
                {
                    OpenAiChunk(new JsonObject { ["role"] = "assistant", ["content"] = "[smug] Готово" }),
                    OpenAiChunk(new JsonObject { ["content"] = ", семпай~" }),
                    OpenAiChunk(new JsonObject(), "stop"),
                    "data: [DONE]\n\n",
                };
            }
            var args = new JsonObject { ["text"] = "Выпить воды", ["in_minutes"] = 5 }.ToJsonString();
            return new[]
            {
                OpenAiChunk(new JsonObject { ["role"] = "assistant", ["content"] = "[think] Ставлю. " }),
                OpenAiChunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["id"] = "call_1", ["type"] = "function", ["function"] = new JsonObject { ["name"] = "create_reminder", ["arguments"] = "" } }) }),
                OpenAiChunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = args[..12] } }) }),
                OpenAiChunk(new JsonObject { ["tool_calls"] = new JsonArray(new JsonObject { ["index"] = 0, ["function"] = new JsonObject { ["arguments"] = args[12..] } }) }),
                OpenAiChunk(new JsonObject(), "tool_calls"),
                "data: [DONE]\n\n",
            };
        });
        Environment.SetEnvironmentVariable("VERA_OPENAI_BASE_URL", server.Base + "/v1");
        try
        {
            var (core, events) = NewCore("openai", "mock-model", "openai");
            await core.Agent.SendAsync("Напомни через 5 минут выпить воды");

            var reminder = Assert.Single(core.Reminders.List());
            Assert.Equal("Выпить воды", reminder.Text);
            Assert.Equal("ai", reminder.Source);
            Assert.Equal(2, server.Requests.Count);
            Assert.Equal("/v1/chat/completions", server.Requests[0].Path);
            var roles = server.Requests[1].Body["messages"]!.AsArray().Select(m => m!["role"]!.GetValue<string>()).ToArray();
            Assert.Equal(new[] { "system", "user", "assistant", "tool" }, roles);
            Assert.Contains(events, e => e is EmotionChanged { Emotion: "think" });
            Assert.Contains(events, e => e is ToolActivity { Running: false, Kind: "reminder" });
            var end = Assert.Single(events.OfType<TurnEnded>());
            Assert.Equal("smug", end.Emotion);
            Assert.Contains("Готово, семпай~", end.Text);
            Assert.DoesNotContain("[smug]", end.Text);
            var visible = core.Agent.GetVisibleHistory();
            Assert.Equal(2, visible.Count);
            Assert.Single(visible[1].Tools);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERA_OPENAI_BASE_URL", null);
        }
    }

    // ------------------------------------------------------------------ Anthropic
    private static string Ev(string type, JsonObject data)
    {
        data["type"] = type;
        return $"event: {type}\ndata: {data.ToJsonString()}\n\n";
    }

    private static IEnumerable<string> AnthropicResponse(int n, bool withTool, string text)
    {
        yield return Ev("message_start", new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["id"] = "msg_" + n, ["type"] = "message", ["role"] = "assistant", ["model"] = "claude-opus-5-5", ["content"] = new JsonArray(),
                ["stop_reason"] = null, ["stop_sequence"] = null, ["usage"] = new JsonObject { ["input_tokens"] = 5, ["output_tokens"] = 1 },
            },
        });
        yield return Ev("content_block_start", new JsonObject { ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" } });
        yield return Ev("content_block_delta", new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = "SIG" + n } });
        yield return Ev("content_block_stop", new JsonObject { ["index"] = 0 });
        yield return Ev("content_block_start", new JsonObject { ["index"] = 1, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } });
        foreach (var part in text.Chunk(5)) yield return Ev("content_block_delta", new JsonObject { ["index"] = 1, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = new string(part) } });
        yield return Ev("content_block_stop", new JsonObject { ["index"] = 1 });
        var stop = "end_turn";
        if (withTool)
        {
            var args = new JsonObject { ["text"] = "Позвонить маме", ["datetime"] = "2031-05-01T18:00" }.ToJsonString();
            yield return Ev("content_block_start", new JsonObject { ["index"] = 2, ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = "toolu_01", ["name"] = "create_reminder", ["input"] = new JsonObject() } });
            yield return Ev("content_block_delta", new JsonObject { ["index"] = 2, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args[..20] } });
            yield return Ev("content_block_delta", new JsonObject { ["index"] = 2, ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = args[20..] } });
            yield return Ev("content_block_stop", new JsonObject { ["index"] = 2 });
            stop = "tool_use";
        }
        yield return Ev("message_delta", new JsonObject { ["delta"] = new JsonObject { ["stop_reason"] = stop, ["stop_sequence"] = null }, ["usage"] = new JsonObject { ["output_tokens"] = 20 } });
        yield return Ev("message_stop", new JsonObject());
    }

    [Fact]
    public async Task AnthropicToolLoopKeepsThinkingInTurnAndDropsItLater()
    {
        var count = 0;
        using var server = new MockServer((path, body) =>
        {
            var n = Interlocked.Increment(ref count);
            var last = body["messages"]!.AsArray()[^1]!;
            var isToolResult = last["content"] is JsonArray arr && arr.Any(b => b?["type"]?.GetValue<string>() == "tool_result");
            return AnthropicResponse(n, withTool: n == 1, text: isToolResult || n > 2 ? "[happy] Готово~ Не забудь!" : "[think] Ставлю. ").ToList();
        });
        Environment.SetEnvironmentVariable("VERA_ANTHROPIC_BASE_URL", server.Base);
        try
        {
            var (core, events) = NewCore("anthropic", "claude-opus-5-5", "anthropic");
            await core.Agent.SendAsync("Напомни 1 мая 2031 в 18:00 позвонить маме");
            await core.Agent.SendAsync("спасибо");

            Assert.Equal(3, server.Requests.Count);
            var r1 = server.Requests[0];
            Assert.StartsWith("/v1/messages", r1.Path);
            Assert.Contains("server-side-fallback-2026-07-01", r1.Beta ?? "");
            Assert.Equal("default", r1.Body["fallbacks"]!.GetValue<string>());
            Assert.Equal("adaptive", r1.Body["thinking"]!["type"]!.GetValue<string>());
            Assert.Equal("low", r1.Body["output_config"]!["effort"]!.GetValue<string>());
            Assert.True(r1.Body["stream"]!.GetValue<bool>());
            Assert.All(r1.Body["tools"]!.AsArray(), t => Assert.True(t!["eager_input_streaming"]!.GetValue<bool>()));

            // внутри хода ответ модели (с подписанным thinking) возвращается как есть
            var r2 = server.Requests[1].Body["messages"]!.AsArray();
            Assert.Equal(new[] { "user", "assistant", "user" }, r2.Select(m => m!["role"]!.GetValue<string>()));
            var asst = r2[1]!["content"]!.AsArray();
            Assert.Equal("thinking", asst[0]!["type"]!.GetValue<string>());
            Assert.Equal("SIG1", asst[0]!["signature"]!.GetValue<string>());
            Assert.Equal("tool_use", asst[2]!["type"]!.GetValue<string>());
            Assert.Equal("Позвонить маме", asst[2]!["input"]!["text"]!.GetValue<string>());

            // следующий ход: thinking прошлых ходов не воспроизводится, system не меняется
            var r3 = server.Requests[2].Body["messages"]!.AsArray();
            Assert.DoesNotContain(r3.SelectMany(m => m!["content"]!.AsArray()), b => b!["type"]!.GetValue<string>() == "thinking");
            Assert.Equal(r1.Body["system"]!.ToJsonString(), server.Requests[2].Body["system"]!.ToJsonString());

            var reminder = Assert.Single(core.Reminders.List());
            Assert.Equal("Позвонить маме", reminder.Text);
            Assert.Equal(18, reminder.Due.ToLocalTime().Hour);
            Assert.Equal(2, events.OfType<TurnEnded>().Count());
            Assert.Equal("happy", events.OfType<TurnEnded>().Last().Emotion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERA_ANTHROPIC_BASE_URL", null);
        }
    }

    [Fact]
    public async Task MissingKeyFailsWithFriendlyMessage()
    {
        var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "mobile");
        TurnFailed? failed = null;
        core.Agent.Event += e => { if (e is TurnFailed f) failed = f; };
        await Assert.ThrowsAnyAsync<Exception>(() => core.Agent.SendAsync("привет"));
        Assert.NotNull(failed);
        Assert.Contains("ключ", failed!.Message);
        Assert.False(core.Agent.IsBusy);
    }
}

public class FallbackIntegrationTests
{
    private static string Chunk(JsonObject delta, string? finish = null) =>
        "data: " + new JsonObject
        {
            ["id"] = "x", ["object"] = "chat.completion.chunk", ["created"] = 1, ["model"] = "mock",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["delta"] = delta, ["finish_reason"] = finish }),
        }.ToJsonString() + "\n\n";

    private static IEnumerable<string> Say(string text) => new[]
    {
        Chunk(new JsonObject { ["role"] = "assistant", ["content"] = text }), Chunk(new JsonObject(), "stop"), "data: [DONE]\n\n",
    };

    [Fact]
    public async Task JsonWrittenAsTextIsExecuted()
    {
        using var server = new MockServer((path, body) =>
        {
            var last = body["messages"]!.AsArray()[^1]!;
            var lastText = last["content"]?.ToJsonString() ?? "";
            return lastText.Contains("tool_response")
                ? Say("[happy] Готово, напомню!")
                : Say("[think] {\"name\": \"create_reminder\", \"arguments\": {\"text\": \"Позвонить\", \"in_minutes\": 10}}");
        });
        Environment.SetEnvironmentVariable("VERA_OPENAI_BASE_URL", server.Base + "/v1");
        try
        {
            var secrets = new MemSecrets();
            secrets.Set("openai", "sk-test");
            var core = new VeraCore(new MemStorage(), secrets, null, "desktop");
            core.Settings.Update(s => { s.Provider = "openai"; s.Models["openai"] = "mock-model"; });
            var events = new List<AgentEvent>();
            core.Agent.Event += e => { lock (events) events.Add(e); };
            await core.Agent.SendAsync("напомни через 10 минут позвонить");

            Assert.Equal("Позвонить", Assert.Single(core.Reminders.List()).Text);
            var replaced = Assert.Single(events.OfType<TextReplaced>());
            Assert.Equal("", replaced.Text);
            Assert.Equal(2, server.Requests.Count);
            var second = server.Requests[1].Body["messages"]!.AsArray();
            Assert.Equal("user", second[^1]!["role"]!.GetValue<string>());
            Assert.Contains("tool_response", second[^1]!["content"]!.ToJsonString());
            var end = Assert.Single(events.OfType<TurnEnded>());
            Assert.DoesNotContain("create_reminder", end.Text);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VERA_OPENAI_BASE_URL", null);
        }
    }

    [Fact]
    public async Task ModelWithoutToolsGetsToolsInPrompt()
    {
        using var server = new MockServer((path, body) =>
        {
            if (body["tools"] is JsonArray)
                return new[] { "STATUS:404", "{\"error\":{\"message\":\"No endpoints found that support tool use.\",\"code\":404}}" };
            var last = body["messages"]!.AsArray()[^1]!;
            if ((last["content"]?.ToJsonString() ?? "").Contains("tool_response")) return Say("Поставила~");
            return Say("<tool_call>{\"name\": \"create_reminder\", \"arguments\": {\"text\": \"Чай\", \"in_minutes\": 3}}</tool_call>");
        });
        try
        {
            var core = new VeraCore(new MemStorage(), new MemSecrets(), null, "desktop");
            core.Settings.Update(s => { s.Provider = "custom"; s.CustomBaseUrl = server.Base + "/v1"; s.Models["custom"] = "free-model"; });
            Assert.True(core.ProviderReady);
            await core.Agent.SendAsync("напомни про чай");
            Assert.Equal("Чай", Assert.Single(core.Reminders.List()).Text);
            var noTools = server.Requests.Where(r => r.Body["tools"] is null).ToList();
            Assert.NotEmpty(noTools);
            Assert.Contains("# Инструменты", noTools[0].Body["messages"]![0]!["content"]!.GetValue<string>());
        }
        finally
        {
            server.Dispose();
        }
    }
}
