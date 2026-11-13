using System.IO.Compression;

namespace Vera.LocalAI;

/// <summary>Состояние загрузки: сколько скачано из скольки, скорость.</summary>
public sealed record DownloadProgress(string AssetId, long Done, long Total, double BytesPerSecond, string Stage = "download")
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

/// <summary>
/// Папка с моделями и их скачивание. Раскладка:
/// <code>
/// root/llm/*.gguf                       языковые модели (свои файлы тоже можно положить сюда)
/// root/voice/vosk-small-ru/...          Vosk
/// root/voice/ggml-*.bin                 Whisper
/// root/voice/piper/piper.exe            Piper + голоса root/voice/*.onnx(.json)
/// root/backends/runtimes/win-x64/native/vulkan/llama.dll   ускорители llama.cpp
/// </code>
/// </summary>
public sealed class ModelStore
{
    private readonly HttpClient _http;

    public ModelStore(string root, HttpClient http)
    {
        Root = root;
        _http = http;
    }

    public string Root { get; }
    public string LlmDir => Path.Combine(Root, "llm");
    public string VoiceDir => Path.Combine(Root, "voice");
    /// <summary>Каталог поиска нативных библиотек LLamaSharp (внутри — runtimes/&lt;rid&gt;/native/&lt;вариант&gt;).</summary>
    public string BackendsDir => Path.Combine(Root, "backends");

    /// <summary>Путь к главному файлу/папке ассета после установки.</summary>
    public string PathOf(AssetInfo a) => a.Kind switch
    {
        AssetKind.Llm => Path.Combine(LlmDir, a.Files[0].FileName),
        AssetKind.Backend => Path.Combine(Root, a.Folder!, "win-x64", "native", a.Id["backend-".Length..]),
        _ when a.Extract => Path.Combine(VoiceDir, a.Folder!),
        _ => Path.Combine(VoiceDir, a.Files[0].FileName),
    };

    public string PiperExe => Path.Combine(VoiceDir, "piper", OperatingSystem.IsWindows() ? "piper.exe" : "piper");

    public bool IsInstalled(AssetInfo a)
    {
        var p = PathOf(a);
        if (a.Extract) return Directory.Exists(p) && Directory.EnumerateFileSystemEntries(p).Any();
        return a.Files.All(f => File.Exists(Path.Combine(Path.GetDirectoryName(p)!, f.FileName)));
    }

    public bool IsInstalled(string id) => ModelCatalog.Get(id) is { } a && IsInstalled(a);

    /// <summary>Все GGUF-файлы в папке llm (скачанные и свои).</summary>
    public IReadOnlyList<FileInfo> InstalledLlms()
    {
        if (!Directory.Exists(LlmDir)) return Array.Empty<FileInfo>();
        return new DirectoryInfo(LlmDir).EnumerateFiles("*.gguf").OrderBy(f => f.Length).ToList();
    }

    /// <summary>Сколько места занимают модели.</summary>
    public long UsedBytes() => Directory.Exists(Root)
        ? new DirectoryInfo(Root).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
        : 0;

    public void Delete(AssetInfo a)
    {
        var p = PathOf(a);
        if (a.Extract && Directory.Exists(p)) Directory.Delete(p, recursive: true);
        else foreach (var f in a.Files)
        {
            var fp = Path.Combine(Path.GetDirectoryName(p)!, f.FileName);
            if (File.Exists(fp)) File.Delete(fp);
            if (File.Exists(fp + ".part")) File.Delete(fp + ".part");
        }
    }

    /// <summary>Скачать (с докачкой после обрыва) и, если нужно, распаковать.</summary>
    public async Task InstallAsync(AssetInfo a, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var target = PathOf(a);
        var dir = a.Extract ? Path.Combine(Root, "downloads") : Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(dir);
        var total = a.SizeMB * 1024 * 1024;
        long before = 0;
        foreach (var f in a.Files)
        {
            var dest = Path.Combine(dir, f.FileName);
            var baseDone = before;
            var p = progress is null ? null : new Progress<DownloadProgress>(x =>
                progress.Report(x with { AssetId = a.Id, Done = baseDone + x.Done, Total = Math.Max(total, baseDone + x.Total) }));
            await ModelDownloader.DownloadAsync(_http, f.Url, dest, p, ct).ConfigureAwait(false);
            before += new FileInfo(dest).Length;
        }
        if (!a.Extract) return;

        progress?.Report(new DownloadProgress(a.Id, before, before, 0, "extract"));
        var archive = Path.Combine(dir, a.Files[0].FileName);
        var outDir = a.Kind == AssetKind.Backend ? Path.Combine(Root, a.Folder!) : target;
        await Task.Run(() => ExtractTo(archive, a.ExtractPrefix ?? "", outDir), ct).ConfigureAwait(false);
        File.Delete(archive);
    }

    /// <summary>Распаковать из zip только файлы под prefix (без него в путях) — защищено от «../» в архиве.</summary>
    internal static void ExtractTo(string zipPath, string prefix, string outDir)
    {
        var full = Path.GetFullPath(outDir);
        var tmp = full.TrimEnd(Path.DirectorySeparatorChar) + ".tmp";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var e in zip.Entries)
            {
                var name = e.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = name[prefix.Length..];
                if (rel.Length == 0) continue;
                var dest = Path.GetFullPath(Path.Combine(tmp, rel));
                if (!dest.StartsWith(tmp + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (name.EndsWith('/')) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, overwrite: true);
            }
        }
        // ускорители складываются в общую папку runtimes — переносим поверх, не стирая другие
        MergeInto(tmp, full);
        Directory.Delete(tmp, true);
    }

    private static void MergeInto(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.GetDirectories(from)) MergeInto(d, Path.Combine(to, Path.GetFileName(d)));
        foreach (var f in Directory.GetFiles(from)) File.Move(f, Path.Combine(to, Path.GetFileName(f)), overwrite: true);
    }
}

/// <summary>Скачивание большого файла: поток в .part, докачка через Range, файл появляется только целиком.</summary>
public static class ModelDownloader
{
    public static async Task DownloadAsync(HttpClient http, string url, string dest, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        if (File.Exists(dest)) return;
        var part = dest + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && have > 0)
        {
            // .part уже целиком скачан
            File.Move(part, dest, overwrite: true);
            return;
        }
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Не удалось скачать {Path.GetFileName(dest)}: сервер ответил {(int)resp.StatusCode}.", null, resp.StatusCode);

        var resumed = resp.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!resumed) have = 0; // сервер не умеет Range — начинаем заново
        var total = resp.Content.Headers.ContentLength is { } len ? len + have : -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fs = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buf = new byte[1 << 16];
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long done = have, sinceReport = 0, startDone = have;
            var lastReport = TimeSpan.Zero;
            int n;
            while ((n = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
            {
                await fs.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                sinceReport += n;
                if (progress is not null && sw.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
                {
                    lastReport = sw.Elapsed;
                    var speed = (done - startDone) / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                    progress.Report(new DownloadProgress("", done, total, speed));
                    sinceReport = 0;
                }
            }
            progress?.Report(new DownloadProgress("", done, total < 0 ? done : total, 0));
            if (total > 0 && done < total)
                throw new IOException($"Загрузка {Path.GetFileName(dest)} оборвалась ({done} из {total} байт). Нажми ещё раз — докачаю.");
        }
        File.Move(part, dest, overwrite: true);
    }
}
