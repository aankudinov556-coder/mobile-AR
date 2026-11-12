using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using Vera.Core;

namespace Vera.Desktop.Platform;

/// <summary>Файлы данных в %APPDATA%\VeraAssistant. Запись атомарная: временный файл + замена.</summary>
public sealed class FileStorage : IVeraStorage
{
    private static readonly UTF8Encoding Utf8 = new(false);
    private readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    public FileStorage(string directory)
    {
        Directory = directory;
        System.IO.Directory.CreateDirectory(directory);
    }

    public string Directory { get; }

    /// <summary>Папка данных по умолчанию (VERA_USER_DATA — для автотестов).</summary>
    public static string DefaultDirectory()
    {
        var custom = Environment.GetEnvironmentVariable("VERA_USER_DATA");
        if (!string.IsNullOrWhiteSpace(custom)) return custom;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VeraAssistant");
    }

    public string PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains(".."))
            throw new ArgumentException("Недопустимое имя файла: " + name);
        return Path.Combine(Directory, name);
    }

    private object LockFor(string name)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(name, out var l)) _locks[name] = l = new object();
            return l;
        }
    }

    public string? ReadText(string name)
    {
        var path = PathFor(name);
        lock (LockFor(name))
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        // первая запись могла оборваться до переименования — подберём временную копию
                        var tmp = path + ".tmp";
                        return File.Exists(tmp) ? File.ReadAllText(tmp, Encoding.UTF8) : null;
                    }
                    return File.ReadAllText(path, Encoding.UTF8);
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(40);
                }
            }
        }
    }

    public void WriteText(string name, string content)
    {
        var path = PathFor(name);
        lock (LockFor(name))
        {
            System.IO.Directory.CreateDirectory(Directory);
            var tmp = path + ".tmp";
            var bytes = Utf8.GetBytes(content ?? "");
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    // MoveFileEx(REPLACE_EXISTING) — замена одним действием на том же томе
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    // файл держит антивирус / индексатор — подождём чуть-чуть
                    Thread.Sleep(50 * (attempt + 1));
                }
                catch (UnauthorizedAccessException) when (attempt < 4)
                {
                    Thread.Sleep(50 * (attempt + 1));
                }
            }
        }
    }
}
