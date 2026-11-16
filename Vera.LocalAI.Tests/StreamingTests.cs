using System.Diagnostics;
using System.Formats.Tar;
using ICSharpCode.SharpZipLib.BZip2;
using Vera.Core.Speech;
using Vera.LocalAI.Voice;
using Xunit;

namespace Vera.LocalAI.Tests;

public class StreamingTests
{
    [Fact]
    public void TarBz2ExtractsOnlyWantedFilesAndBlocksEscapes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vera-tar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var archive = Path.Combine(dir, "m.tar.bz2");
            using (var fs = File.Create(archive))
            using (var bz = new BZip2OutputStream(fs))
            using (var tar = new TarWriter(bz))
            {
                void Add(string name, string text)
                {
                    var e = new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
                    tar.WriteEntry(e);
                }
                Add("pkg/encoder-x.int8.onnx", "enc");
                Add("pkg/encoder-x.onnx", "big");
                Add("pkg/tokens.txt", "tok");
                Add("pkg/../evil.int8.onnx", "bad");
                Add("other/joiner.int8.onnx", "wrong prefix");
            }
            var outDir = Path.Combine(dir, "out");
            ModelStore.ExtractTo(archive, "pkg/", outDir, new[] { ".int8.onnx", "tokens.txt" });
            var files = Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order().ToArray();
            Assert.Equal(new[] { "encoder-x.int8.onnx", "tokens.txt" }, files);
            Assert.False(File.Exists(Path.Combine(dir, "evil.int8.onnx")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CatalogHasStreamModels()
    {
        Assert.Equal("stream-ru", ModelCatalog.StreamFor("ru")!.Id);
        Assert.Equal("stream-en", ModelCatalog.StreamFor("en")!.Id);
        Assert.Equal("stream-ru", ModelCatalog.StreamFor("")!.Id);
        Assert.All(new[] { "stream-ru", "stream-en" }, id =>
        {
            var a = ModelCatalog.Get(id)!;
            Assert.True(a.Extract);
            Assert.EndsWith(".tar.bz2", a.Files[0].FileName);
            Assert.StartsWith("https://github.com/k2-fsa/sherpa-onnx/releases/download/", a.Files[0].Url);
            Assert.Contains("tokens.txt", a.ExtractOnly!);
        });
    }

    [Theory]
    [InlineData("сейчас к тебе приедет", "Сейчас к тебе приедет")]
    [InlineData("WHAT TIME IS IT  I'M LATE", "What time is it I'm late")]
    [InlineData("CAN I OPEN IT", "Can I open it")]
    [InlineData("  ", "")]
    public void TextIsNormalized(string raw, string expected) => Assert.Equal(expected, StreamingSpeech.Normalize(raw));

    /// <summary>Настоящая модель: VERA_STREAM_RU = папка T-one (sherpa-onnx-streaming-t-one-russian-…). Без неё тест молча проходит.</summary>
    [Fact]
    public void RussianModelIsFastAndAccurate()
    {
        var dir = Environment.GetEnvironmentVariable("VERA_STREAM_RU");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
        using var rec = new StreamingSpeech(dir);
        Assert.Equal("t-one", rec.Kind);
        var (samples, rate) = WavEncoder.Decode(File.ReadAllBytes(Path.Combine(dir, "0.wav")));
        using var s = rec.Start();
        var partials = 0;
        s.PartialChanged += _ => partials++;
        var chunk = rate / 50; // по 20 мс, как микрофон
        for (var i = 0; i < samples.Length; i += chunk)
            s.Feed(samples.AsSpan(i, Math.Min(chunk, samples.Length - i)).ToArray(), rate);
        Assert.Contains("бригада", s.Partial.ToLowerInvariant()); // слова видны ещё до конца фразы
        var sw = Stopwatch.StartNew();
        var text = s.Finish();
        sw.Stop();
        Assert.StartsWith("Сейчас к тебе приедет бригада", text);
        Assert.True(partials > 2);
        Assert.True(sw.ElapsedMilliseconds < 300, $"итог через {sw.ElapsedMilliseconds} мс");
        Assert.Equal(text, rec.Transcribe(samples, rate));
    }
}
