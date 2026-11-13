using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Vera.Core;
using Vera.Core.Agent;
using Vera.Core.Documents;
using Vera.Core.Speech;
using Vera.Core.Sprites;
using Vera.Core.Tools;
using Xunit;

namespace Vera.Core.Tests;

public class ToolCallParserTests
{
    private static readonly string[] Known = { "create_reminder", "open_app", "web_search" };

    [Fact]
    public void ParsesToolCallTags()
    {
        var text = "Сейчас поставлю!\n<tool_call>\n{\"name\": \"create_reminder\", \"arguments\": {\"text\": \"Вода\", \"in_minutes\": 5}}\n</tool_call>";
        Assert.True(ToolCallParser.TryExtract(text, Known, out var calls, out var cleaned));
        var c = Assert.Single(calls);
        Assert.Equal("create_reminder", c.Name);
        Assert.Equal(5, c.Args.Num("in_minutes"));
        Assert.Equal("Сейчас поставлю!", cleaned);
    }

    [Fact]
    public void ParsesBareJsonAndFence()
    {
        Assert.True(ToolCallParser.TryExtract("{\"name\": \"open_app\", \"arguments\": {\"name\": \"Telegram\"}}", Known, out var calls, out var cleaned));
        Assert.Equal("Telegram", calls[0].Args.Str("name"));
        Assert.Equal("", cleaned);
        Assert.True(ToolCallParser.TryExtract("Ок\n```json\n{\"name\": \"open_app\", \"parameters\": {\"name\": \"Steam\"}}\n```", Known, out calls, out cleaned));
        Assert.Equal("Steam", calls[0].Args.Str("name"));
        Assert.Equal("Ок", cleaned);
    }

    [Fact]
    public void ParsesOpenAiStyleAndStringArguments()
    {
        var json = "{\"tool_calls\":[{\"type\":\"function\",\"function\":{\"name\":\"web_search\",\"arguments\":\"{\\\"query\\\":\\\"погода\\\"}\"}}]}";
        Assert.True(ToolCallParser.TryExtract(json, Known, out var calls, out _));
        Assert.Equal("погода", calls[0].Args.Str("query"));
    }

    [Fact]
    public void IgnoresUnknownToolsAndExamplesInsideLongText()
    {
        Assert.False(ToolCallParser.TryExtract("{\"name\": \"rm_rf\", \"arguments\": {}}", Known, out _, out _));
        var longText = new string('а', 400) + " Пример: {\"name\": \"open_app\", \"arguments\": {\"name\": \"x\"}}";
        Assert.False(ToolCallParser.TryExtract(longText, Known, out _, out _));
        Assert.True(ToolCallParser.TryExtract(longText, Known, out _, out _, strict: false));
    }

    [Fact]
    public void StripsThinking() => Assert.Equal("Ответ", ToolCallParser.StripThinking("<think>хм, подумаем</think>Ответ"));

    [Fact]
    public void ToolPromptDescribesTools()
    {
        var d = ToolPrompt.Describe(ToolRegistry.CoreDefinitions().ToList());
        Assert.Contains("<tool_call>", d);
        Assert.Contains("create_reminder", d);
        Assert.Contains("обязательно", d);
    }
}

public class DocumentTests
{
    [Fact]
    public void ReadsUtf8AndCp1251Text()
    {
        Assert.Equal("Привет", DocumentReader.Read(Encoding.UTF8.GetBytes("Привет"), "a.txt").Text);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Assert.Equal("Привет", DocumentReader.Read(Encoding.GetEncoding(1251).GetBytes("Привет"), "b.txt").Text);
    }

    [Fact]
    public void ReadsDocx()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open());
            w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                    "<w:p><w:r><w:t>Договор</w:t></w:r></w:p><w:p><w:r><w:t>Сумма: 100</w:t></w:r></w:p></w:body></w:document>");
        }
        var doc = DocumentReader.Read(ms.ToArray(), "c.docx");
        Assert.Contains("Договор", doc.Text);
        Assert.Contains("Сумма: 100", doc.Text);
        Assert.Contains("\n", doc.Text);
    }

    [Fact]
    public void LongDocumentIsSplit()
    {
        var doc = new Vera.Core.Providers.DocumentText("d_1", "big.txt", new string('x', 100_000));
        var msg = DocumentReader.ForMessage(doc);
        Assert.True(msg.Text.Length < 70_000);
        Assert.Contains("read_document", msg.Text);
        var (text, part, parts) = DocumentReader.GetPart(doc.Text, 4);
        Assert.Equal(4, part);
        Assert.Equal(4, parts);
        Assert.Equal(10_000, text.Length);
    }

    [Fact]
    public void UnsupportedThrows() => Assert.Throws<ArgumentException>(() => DocumentReader.Read(new byte[] { 1, 2 }, "x.exe"));
}

public class SearchTests
{
    [Fact]
    public void ParsesDuckDuckGoHtml()
    {
        const string html = """
            <div class="result"><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fexample.com%2Fpage&amp;rut=x">Пример <b>страницы</b></a>
            <a class="result__snippet" href="#">Короткое <b>описание</b></a></div>
            <div class="result"><a rel="nofollow" class="result__a" href="https://second.org/">Второй</a>
            <a class="result__snippet" href="#">Ещё текст</a></div>
            """;
        var hits = WebSearch.ParseDuckDuckGo(html);
        Assert.Equal(2, hits.Count);
        Assert.Equal("https://example.com/page", hits[0].Url);
        Assert.Equal("Пример страницы", hits[0].Title);
        Assert.Equal("Короткое описание", hits[0].Snippet);
        Assert.Equal("https://second.org/", hits[1].Url);
    }

    [Fact]
    public void ExtractsMainText()
    {
        var html = "<html><head><title>Заголовок</title><script>var x=1;</script></head><body><nav>Меню</nav>" +
                   "<article><h1>Статья</h1><p>" + new string('т', 500) + "</p><p>Второй абзац</p></article><footer>подвал</footer></body></html>";
        var (title, text) = HtmlText.Extract(html);
        Assert.Equal("Заголовок", title);
        Assert.Contains("Второй абзац", text);
        Assert.DoesNotContain("var x", text);
        Assert.DoesNotContain("Меню", text);
        Assert.DoesNotContain("подвал", text);
    }
}

public class UsageTests
{
    [Fact]
    public void RecordsTokensAndCost()
    {
        var u = new UsageService(new MemStorage());
        u.Record("anthropic", "claude-opus-5-5", 1_000_000, 100_000);
        u.Record("custom", "qwen", 500, 500);
        var today = u.Today();
        Assert.Equal(1_000_500, today.InputTokens);
        Assert.Equal(2, today.Requests);
        Assert.Equal(6.0, today.Cost!.Value, 3); // $4 вход + $2 выход
        Assert.False(today.CostPartial);
        Assert.Equal(2, u.MonthByProvider().Count);
    }

    [Fact]
    public void UnknownPriceIsPartial()
    {
        var u = new UsageService(new MemStorage());
        u.Record("openai", "gpt-unknown", 10, 10);
        Assert.Null(u.Today().Cost);
    }
}

public class VadHookTests
{
    [Fact]
    public void SpeechProbabilityOverridesEnergy()
    {
        var rec = new VoiceRecorder(() => 0.5, () => 400);
        byte[]? got = null;
        rec.Segment += (wav, _, _) => got = wav;
        var speaking = false;
        rec.SpeechProbability = (_, _) => speaking ? 0.9f : 0.05f;
        rec.StartCommand();
        var quiet = new float[320]; // нулевая громкость, но «нейросеть» слышит речь
        speaking = true;
        for (var i = 0; i < 40; i++) rec.Feed(quiet, 16000);
        speaking = false;
        for (var i = 0; i < 30; i++) rec.Feed(quiet, 16000);
        Assert.NotNull(got);
        RecorderState? last = null;
        rec.StateChanged += (s, _) => last = s;
        rec.EndProcessing();
        Assert.Equal(RecorderState.Off, last);
    }
}

public class SkinTests
{
    [Fact]
    public void FloorFrame()
    {
        var f = VeraSprite.Compose("neutral", PetMode.Idle, false, false, false, Posture.Floor);
        Assert.Equal("floor_idle", f.Layers[0]);
        Assert.Contains("front", f.Layers);
        var dragged = VeraSprite.Compose("neutral", PetMode.Idle, false, false, true, Posture.Floor);
        Assert.StartsWith("base_", dragged.Layers[0]);
    }

    [Theory]
    [InlineData("neutral", PetMode.Idle, Posture.Stand, "shrug")]
    [InlineData("happy", PetMode.Talking, Posture.Stand, "laugh")]
    [InlineData("smug", PetMode.Idle, Posture.Stand, "giggle")]
    [InlineData("neutral", PetMode.Thinking, Posture.Stand, "think_sit")]
    [InlineData("neutral", PetMode.Sleeping, Posture.Stand, "lie")]
    [InlineData("neutral", PetMode.Idle, Posture.Floor, "sit")]
    [InlineData("neutral", PetMode.Talking, Posture.Floor, "shrug")]
    public void HaloPoses(string emotion, PetMode mode, Posture posture, string expected) =>
        Assert.Equal(expected, Skins.HaloPose(emotion, mode, posture).Name);

    [Fact]
    public void HaloFilesExist()
    {
        var dir = Path.Combine(SpriteTests.FindRoot(), "assets", "skins", "halo");
        foreach (var name in Skins.HaloPoses.Keys) Assert.True(File.Exists(Path.Combine(dir, name + ".png")), name);
    }
}

public class SettingsTransferTests
{
    [Fact]
    public void ExportImportKeepsLocalThings()
    {
        var a = new SettingsService(new MemStorage());
        a.Update(s => { s.Persona = "kind"; s.Pet.Scale = 5; s.Pet.X = 100; s.Local.ModelPath = @"C:\a.gguf"; });
        var json = a.Export();
        Assert.DoesNotContain("sk-", json);

        var b = new SettingsService(new MemStorage());
        b.Update(s => { s.Pet.X = 7; s.Local.ModelPath = @"D:\mine.gguf"; });
        var changed = 0;
        b.Changed += _ => changed++;
        b.Import(json);
        Assert.Equal("kind", b.Current.Persona);
        Assert.Equal(5, b.Current.Pet.Scale);
        Assert.Equal(7, b.Current.Pet.X);                    // позиция — своя
        Assert.Equal(@"D:\mine.gguf", b.Current.Local.ModelPath); // путь к модели — свой
        Assert.Equal(1, changed);
        Assert.Throws<FormatException>(() => b.Import("не json"));
    }

    [Fact]
    public void ResetRestoresDefaults()
    {
        var s = new SettingsService(new MemStorage());
        s.Update(x => { x.Persona = "kind"; x.Pet.Y = 42; });
        s.Reset();
        Assert.Equal(new VeraSettings().Persona, s.Current.Persona);
        Assert.Equal(42, s.Current.Pet.Y);
        Assert.False(s.Current.FirstRun);
        Assert.True(s.Current.Models.ContainsKey("local"));
    }

    [Fact]
    public void PresetsApply()
    {
        var s = new SettingsService(new MemStorage());
        s.Update(SettingsPresets.All.First(p => p.Id == "offline").Apply);
        Assert.Equal("local", s.Current.Provider);
        Assert.Equal("piper", s.Current.Tts.Engine);
        Assert.Equal("whisper", s.Current.Stt.Provider);
        Assert.Equal(new[] { "openai" }, SettingsPresets.MissingKeys("quality", k => k == "anthropic"));
        Assert.Empty(SettingsPresets.MissingKeys("offline", _ => false));
    }
}
