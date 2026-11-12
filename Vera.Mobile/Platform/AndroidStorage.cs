using Vera.Core;

namespace Vera.Mobile.Platform;

/// <summary>Файлы данных в FileSystem.AppDataDirectory, запись атомарная (tmp → move).</summary>
public sealed class AndroidStorage : IVeraStorage
{
    private readonly object _gate = new();

    public AndroidStorage()
    {
        string dir;
        try { dir = FileSystem.AppDataDirectory; }
        catch { dir = Android.App.Application.Context.FilesDir!.AbsolutePath; }
        if (string.IsNullOrEmpty(dir)) dir = Android.App.Application.Context.FilesDir!.AbsolutePath;
        Directory = System.IO.Path.Combine(dir, "vera");
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    private string PathOf(string name)
    {
        var safe = string.Concat(name.Select(c => System.IO.Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return System.IO.Path.Combine(Directory, safe);
    }

    public string? ReadText(string name)
    {
        var p = PathOf(name);
        lock (_gate)
        {
            try
            {
                if (File.Exists(p)) return File.ReadAllText(p);
                // запись могла оборваться между записью tmp и переименованием
                var tmp = p + ".tmp";
                return File.Exists(tmp) ? File.ReadAllText(tmp) : null;
            }
            catch (IOException) { return null; }
        }
    }

    public void WriteText(string name, string content)
    {
        var p = PathOf(name);
        var tmp = p + ".tmp";
        lock (_gate)
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(true);
            }
            File.Move(tmp, p, overwrite: true);
        }
    }
}
