using System.Text.Json.Nodes;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Markdown;
using Vera.Core.Providers;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Core.Tools;
using Xunit;

namespace Vera.Core.Tests;

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

public class ReminderTests
{
    [Fact]
    public void DailyMovesToTomorrow()
    {
        var now = new DateTimeOffset(2026, 11, 12, 10, 0, 0, TimeSpan.Zero).ToLocalTime();
        var due = now.AddHours(-1);
        var n = ReminderService.NextDue(due, "daily", now)!.Value;
        Assert.Equal(due.AddDays(1), n);
    }

    [Fact]
    public void WeekdaysSkipWeekend()
    {
        var fri = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 13, 9, 0, 0)));
        Assert.Equal(DayOfWeek.Friday, fri.ToLocalTime().DayOfWeek);
        var n = ReminderService.NextDue(fri, "weekdays", fri.AddSeconds(1))!.Value;
        Assert.Equal(DayOfWeek.Monday, n.ToLocalTime().DayOfWeek);
    }

    [Fact]
    public void MonthlyAddsMonth()
    {
        var t = new DateTimeOffset(2026, 1, 15, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(2, ReminderService.NextDue(t, "monthly", t.AddSeconds(1))!.Value.ToLocalTime().Month);
    }

    [Fact]
    public void NoRepeatReturnsNull() =>
        Assert.Null(ReminderService.NextDue(DateTimeOffset.Now.AddHours(-1), "none", DateTimeOffset.Now));

    [Fact]
    public void TickFiresAndMarksDone()
    {
        var r = new ReminderService(new MemStorage());
        var fired = new List<(Reminder, bool)>();
        r.Due += (x, m) => fired.Add((x, m));
        var item = r.Add("тест", DateTimeOffset.Now.AddSeconds(30));
        Assert.Single(r.Upcoming());
        r.Update(item.Id, due: DateTimeOffset.Now.AddSeconds(-5));
        r.Tick();
        Assert.Single(fired);
        Assert.False(fired[0].Item2);
        Assert.True(r.Get(item.Id)!.Done);
        Assert.Empty(r.Upcoming());
    }

    [Fact]
    public void StartupTickMarksMissed()
    {
        var r = new ReminderService(new MemStorage());
        var missed = false;
        r.Due += (_, m) => missed = m;
        var item = r.Add("x", DateTimeOffset.Now.AddMinutes(5));
        r.Update(item.Id, due: DateTimeOffset.Now.AddMinutes(-30));
        r.Tick(startup: true);
        Assert.True(missed);
    }

    [Fact]
    public void RepeatingReschedulesAfterFire()
    {
        var r = new ReminderService(new MemStorage());
        var item = r.Add("каждый час", DateTimeOffset.Now.AddMinutes(10), "hourly");
        r.Update(item.Id, due: DateTimeOffset.Now.AddMinutes(-1));
        r.Tick();
        var after = r.Get(item.Id)!;
        Assert.False(after.Done);
        Assert.True(after.Due > DateTimeOffset.Now);
    }

    [Fact]
    public void PastTimeWithoutRepeatThrows() =>
        Assert.Throws<ArgumentException>(() => new ReminderService(new MemStorage()).Add("x", DateTimeOffset.Now.AddHours(-1)));

    [Fact]
    public void SnoozeReactivates()
    {
        var r = new ReminderService(new MemStorage());
        var x = r.Add("x", DateTimeOffset.Now.AddMinutes(1));
        r.Complete(x.Id);
        Assert.True(r.Get(x.Id)!.Done);
        r.Snooze(x.Id, 10);
        var s = r.Get(x.Id)!;
        Assert.False(s.Done);
        Assert.True(s.Due > DateTimeOffset.Now.AddMinutes(9));
    }

    [Fact]
    public void PersistsAcrossInstances()
    {
        var storage = new MemStorage();
        var r = new ReminderService(storage);
        r.Add("сохранить", DateTimeOffset.Now.AddHours(2), "daily");
        r.Flush();
        var r2 = new ReminderService(storage);
        Assert.Equal("сохранить", Assert.Single(r2.List()).Text);
    }
}

public class ToolTests
{
    [Fact]
    public void ParseDueInMinutes()
    {
        var now = DateTimeOffset.Now;
        var t = ToolRegistry.ParseDue(new JsonObject { ["in_minutes"] = 20 }, now);
        Assert.Equal(now.AddMinutes(20), t);
    }

    [Fact]
    public void ParseDueLocalDateTime()
    {
        var t = ToolRegistry.ParseDue(new JsonObject { ["datetime"] = "2030-01-02T09:30" }).ToLocalTime();
        Assert.Equal(9, t.Hour);
        Assert.Equal(30, t.Minute);
        Assert.Equal(2, t.Day);
    }

    [Fact]
    public void ParseDueGarbageThrows() =>
        Assert.Throws<ArgumentException>(() => ToolRegistry.ParseDue(new JsonObject { ["datetime"] = "завтра" }));

    [Fact]
    public async Task CreateReminderToolAddsReminder()
    {
        var storage = new MemStorage();
        var rem = new ReminderService(storage);
        var tools = new ToolRegistry(rem, new MemoryService(storage), new HttpClient(), null);
        var res = await tools.ExecuteAsync("create_reminder", new JsonObject { ["text"] = "Выпить воды", ["in_minutes"] = 5 }, default);
        Assert.False(res.IsError, res.Text);
        Assert.Equal("reminder", res.Kind);
        Assert.Equal("ai", Assert.Single(rem.List()).Source);
    }

    [Fact]
    public async Task UnknownToolIsError()
    {
        var storage = new MemStorage();
        var tools = new ToolRegistry(new ReminderService(storage), new MemoryService(storage), new HttpClient(), null);
        Assert.True((await tools.ExecuteAsync("nope", new JsonObject(), default)).IsError);
    }

    [Fact]
    public async Task RememberAndForget()
    {
        var storage = new MemStorage();
        var mem = new MemoryService(storage);
        var tools = new ToolRegistry(new ReminderService(storage), mem, new HttpClient(), null);
        await tools.ExecuteAsync("remember", new JsonObject { ["fact"] = "Пользователя зовут Артём" }, default);
        Assert.Single(mem.List());
        Assert.Contains("Артём", mem.ForPrompt());
        await tools.ExecuteAsync("forget", new JsonObject { ["id"] = "Артём" }, default);
        Assert.Empty(mem.List());
    }
}

public class TagFilterTests
{
    [Fact]
    public void StripsTagAcrossChunks()
    {
        string? emo = null;
        var f = new TagFilter(e => emo = e);
        var outText = string.Concat(new[] { "[sm", "ug] Хе", "-хе, ", "семпай~" }.Select(f.Push)) + f.Flush();
        Assert.Equal("smug", emo);
        Assert.Equal("Хе-хе, семпай~", outText);
    }

    [Fact]
    public void LeavesPlainText()
    {
        var f = new TagFilter(_ => { });
        Assert.Equal("Привет мир", f.Push("Привет") + f.Push(" мир") + f.Flush());
    }

    [Fact]
    public void KeepsNonTagBrackets()
    {
        var f = new TagFilter(_ => { });
        Assert.Equal("[1] пункт", f.Push("[1] пункт") + f.Flush());
    }

    [Fact]
    public void Strip() => Assert.Equal("Ура", TagFilter.Strip("[happy] Ура"));
}

public class SpeechTests
{
    [Fact]
    public void ChunkerSplitsSentencesAndSkipsCode()
    {
        var chunks = new List<string>();
        var c = new SentenceChunker(chunks.Add);
        foreach (var piece in "Это первое довольно длинное предложение для теста. А вот и второе предложение! ```js\nlet a=1;\n``` И хвост".Chunk(7))
            c.Push(new string(piece));
        c.End();
        Assert.Contains(chunks, x => x.Contains("первое"));
        Assert.Contains("(код — в чате)", chunks);
        Assert.DoesNotContain(chunks, x => x.Contains("let a"));
        Assert.Contains(chunks, x => x.Contains("хвост"));
    }

    [Fact]
    public void CleanForSpeechRemovesMarkup() =>
        Assert.Equal("Привет, мир ссылка", SpeechService.CleanForSpeech("**Привет**, `мир` https://example.com"));

    [Fact]
    public void WavHeader()
    {
        var wav = WavEncoder.Encode(new float[1600], 16000);
        Assert.Equal(44 + 3200, wav.Length);
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
    }

    [Fact]
    public void MatchWake()
    {
        Assert.Equal("напомни через 5 минут", VoiceRecorder.MatchWake("Вера, напомни через 5 минут", "вера, верочка"));
        Assert.Equal("", VoiceRecorder.MatchWake("Верочка!", "вера, верочка"));
        Assert.Null(VoiceRecorder.MatchWake("Проверка связи", "вера"));
        Assert.Null(VoiceRecorder.MatchWake("Верность важна", "вера"));
    }

    [Fact]
    public void RecorderProducesSegmentAfterSpeechAndSilence()
    {
        var rec = new VoiceRecorder(() => 0.5, () => 600);
        byte[]? got = null;
        var states = new List<RecorderState>();
        rec.Segment += (wav, mode, ms) => got = wav;
        rec.StateChanged += (s, _) => states.Add(s);
        rec.StartCommand();
        var rate = 16000;
        float[] Frame(float amp) => Enumerable.Range(0, 320).Select(i => amp * MathF.Sin(i * 0.3f)).ToArray();
        for (var i = 0; i < 10; i++) rec.Feed(Frame(0f), rate);       // тишина
        for (var i = 0; i < 40; i++) rec.Feed(Frame(0.3f), rate);     // речь 0.8 с
        for (var i = 0; i < 40; i++) rec.Feed(Frame(0f), rate);       // пауза → конец фразы
        Assert.NotNull(got);
        Assert.Contains(RecorderState.Recording, states);
        Assert.Contains(RecorderState.Processing, states);
        Assert.Equal(ListenMode.Off, rec.Mode);
    }

    [Fact]
    public void RecorderTimesOutWithoutSpeech()
    {
        var rec = new VoiceRecorder(() => 0.5, () => 600);
        string? reason = null;
        rec.StateChanged += (_, r) => reason ??= r;
        rec.StartCommand();
        for (var i = 0; i < 400; i++) rec.Feed(new float[320], 16000); // 8 с тишины
        Assert.Equal("timeout", reason);
    }
}

public class MarkdownTests
{
    [Fact]
    public void ParsesBlocks()
    {
        var b = MarkdownLite.Parse("# Заголовок\nТекст **жирный** и `код`\n\n- раз\n- два\n\n```py\nprint(1)\n```");
        Assert.Equal(MdBlockKind.Heading, b[0].Kind);
        Assert.Contains(b[1].Inlines, i => i.Kind == MdInlineKind.Bold && i.Text == "жирный");
        Assert.Contains(b[1].Inlines, i => i.Kind == MdInlineKind.Code && i.Text == "код");
        Assert.Equal(2, b[2].Items.Count);
        Assert.Equal("py", b[3].Language);
        Assert.Equal("print(1)", b[3].Code);
    }

    [Fact]
    public void UnclosedCodeDuringStreaming()
    {
        var b = MarkdownLite.Parse("пример:\n```js\nlet a = 1");
        Assert.Equal(MdBlockKind.Code, b[^1].Kind);
        Assert.Equal("let a = 1", b[^1].Code);
    }

    [Fact]
    public void Links()
    {
        var i = MarkdownLite.ParseInline("см. [сайт](https://example.com) и https://a.b/c.");
        Assert.Equal(2, i.Count(x => x.Kind == MdInlineKind.Link));
    }
}

public class SpriteTests
{
    [Fact]
    public void StandingFrame()
    {
        var f = VeraSprite.Compose("neutral", PetMode.Idle, false, false);
        Assert.Equal(new[] { "base_idle", "eyes_normal", "mouth_smirk", "blush_0", "front" }, f.Layers);
    }

    [Fact]
    public void SittingFrameHasLegsUnderBody()
    {
        var f = VeraSprite.Compose("smug", PetMode.Idle, false, false, sitting: true, legFrame: 2);
        Assert.Equal("legs_sit_2", f.Layers[0]);
        Assert.Equal("sit_giggle", f.Layers[1]);
        Assert.Equal("hand_giggle", f.Layers[^1]);
    }

    [Fact]
    public void TalkingOpensMouthAndDropsHandOverMouth()
    {
        var f = VeraSprite.Compose("smug", PetMode.Talking, false, true);
        Assert.Contains("mouth_open_fang", f.Layers);
        Assert.Contains("base_idle", f.Layers);
    }

    [Fact]
    public void BlinkClosesEyes() =>
        Assert.Contains("eyes_closed", VeraSprite.Compose("neutral", PetMode.Idle, true, false).Layers);

    [Fact]
    public void AllLayerFilesExist()
    {
        var dir = Path.Combine(FindRoot(), "assets", "vera");
        foreach (var name in VeraSprite.AllLayerNames())
            Assert.True(File.Exists(Path.Combine(dir, name + ".png")), name);
    }

    internal static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var d = new DirectoryInfo(Path.GetDirectoryName(here)!);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Directory.Build.props"))) d = d.Parent;
        return d?.FullName ?? throw new DirectoryNotFoundException("repo root");
    }
}

public class HistoryConversionTests
{
    private static readonly List<VeraMessage> History = new()
    {
        new() { Role = "user", Text = "Напомни через 5 минут", Ctx = "Сейчас: …" },
        new()
        {
            Role = "assistant", Text = "Ставлю!",
            ToolCalls = new() { new() { Id = "call_1", Name = "create_reminder", Args = new JsonObject { ["text"] = "т" } }, new() { Id = "call_2", Name = "x" } },
        },
        new() { Role = "tool", ToolCallId = "call_1", Text = "ok" },
        new() { Role = "tool", ToolCallId = "call_2", Text = "shot", Images = new() { new ImageData("image/jpeg", "AAAA") } },
        new() { Role = "assistant", Text = "Готово" },
        new() { Role = "user", Text = "спс" },
    };

    [Fact]
    public void AnthropicGroupsToolResults()
    {
        var n = AnthropicProvider.ToJson(History);
        Assert.Equal(new[] { "user", "assistant", "user", "assistant", "user" }, n.Select(m => m["role"]!.GetValue<string>()));
        var results = n[2]["content"]!.AsArray();
        Assert.Equal(2, results.Count);
        Assert.Equal("tool_result", results[0]!["type"]!.GetValue<string>());
        Assert.Equal("image", results[1]!["content"]![1]!["type"]!.GetValue<string>());
        Assert.Equal("tool_use", n[1]["content"]![1]!["type"]!.GetValue<string>());
        // и SDK принимает это как типизированные параметры
        var typed = System.Text.Json.JsonSerializer.Deserialize<List<Anthropic.Models.Beta.Messages.BetaMessageParam>>(new JsonArray(n.Select(x => (JsonNode)x.DeepClone()).ToArray()).ToJsonString());
        Assert.Equal(5, typed!.Count);
    }

    [Fact]
    public void OpenAiToolMessagesAndImageFollowUp()
    {
        var list = new List<OpenAI.Chat.ChatMessage>();
        OpenAICompatProvider.Append(list, History);
        Assert.IsType<OpenAI.Chat.UserChatMessage>(list[0]);
        var a = Assert.IsType<OpenAI.Chat.AssistantChatMessage>(list[1]);
        Assert.Equal(2, a.ToolCalls.Count);
        Assert.IsType<OpenAI.Chat.ToolChatMessage>(list[2]);
        Assert.IsType<OpenAI.Chat.ToolChatMessage>(list[3]);
        Assert.IsType<OpenAI.Chat.UserChatMessage>(list[4]); // картинка из инструмента
        Assert.Equal(7, list.Count);
    }
}
