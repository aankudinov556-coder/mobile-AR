using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Vera.LocalAI.Voice;

/// <summary>
/// Офлайн-голос Piper: отдельный процесс piper.exe держится запущенным, каждая строка на stdin превращается
/// в WAV-файл в папке, путь к которому piper печатает в stdout. Так голос не перезагружается на каждую фразу.
/// </summary>
public sealed class PiperTts : IDisposable
{
    private readonly string _exe;
    private readonly string _outDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _proc;
    private string? _key;

    /// <param name="exe">путь к piper.exe</param>
    /// <param name="workDir">временная папка для WAV</param>
    public PiperTts(string exe, string workDir)
    {
        _exe = exe;
        _outDir = workDir;
    }

    /// <summary>Синтезировать фразу → WAV. rate: 1 — обычная скорость, 1.3 — быстрее.</summary>
    public async Task<byte[]> SynthesizeAsync(string voiceOnnx, string text, double rate, CancellationToken ct)
    {
        if (!File.Exists(_exe)) throw new FileNotFoundException("Piper не установлен", _exe);
        if (!File.Exists(voiceOnnx)) throw new FileNotFoundException("Голос Piper не скачан", voiceOnnx);
        var line = OneLine(text);
        if (line.Length == 0) return Array.Empty<byte>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var lengthScale = Math.Clamp(1.0 / Math.Clamp(rate, 0.5, 2.0), 0.5, 2.0);
            var key = voiceOnnx + "|" + lengthScale.ToString("0.00", CultureInfo.InvariantCulture);
            if (_proc is null || _proc.HasExited || _key != key) Start(voiceOnnx, lengthScale, key);

            var p = _proc!;
            await p.StandardInput.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await p.StandardInput.FlushAsync(ct).ConfigureAwait(false);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30 + line.Length / 10));
            while (true)
            {
                var outLine = await p.StandardOutput.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (outLine is null) throw new InvalidOperationException("Piper завершился с ошибкой: " + LastError());
                outLine = outLine.Trim();
                if (!outLine.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) continue;
                var bytes = await File.ReadAllBytesAsync(outLine, ct).ConfigureAwait(false);
                try { File.Delete(outLine); } catch { /* файл подчистим при следующем запуске */ }
                return bytes;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Stop();
            throw new TimeoutException("Piper не ответил вовремя.");
        }
        catch (OperationCanceledException)
        {
            // фраза отменена посреди синтеза — процесс мог остаться с недочитанным ответом
            Stop();
            throw;
        }
        finally { _gate.Release(); }
    }

    private readonly StringBuilder _stderr = new();

    private string LastError()
    {
        lock (_stderr) return _stderr.Length == 0 ? "нет подробностей" : _stderr.ToString().Trim()[^Math.Min(300, _stderr.ToString().Trim().Length)..];
    }

    private void Start(string voice, double lengthScale, string key)
    {
        Stop();
        Directory.CreateDirectory(_outDir);
        foreach (var f in Directory.GetFiles(_outDir, "*.wav")) { try { File.Delete(f); } catch { /* занят */ } }
        var psi = new ProcessStartInfo(_exe)
        {
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in Args(voice, lengthScale, _outDir)) psi.ArgumentList.Add(a);
        var p = Process.Start(psi) ?? throw new InvalidOperationException("Не удалось запустить Piper");
        lock (_stderr) _stderr.Clear();
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr) { _stderr.AppendLine(e.Data); if (_stderr.Length > 4000) _stderr.Remove(0, 2000); }
        };
        p.BeginErrorReadLine();
        _proc = p;
        _key = key;
    }

    internal static IEnumerable<string> Args(string voice, double lengthScale, string outDir) => new[]
    {
        "--model", voice,
        "--output_dir", outDir,
        "--length_scale", lengthScale.ToString("0.00", CultureInfo.InvariantCulture),
        "--sentence_silence", "0.15",
    };

    /// <summary>Piper читает по строке на фразу — переводы строк заменяем пробелами.</summary>
    internal static string OneLine(string text) =>
        string.Join(' ', (text ?? "").Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries)).Trim();

    private void Stop()
    {
        var p = _proc;
        _proc = null;
        _key = null;
        if (p is null) return;
        try { if (!p.HasExited) { p.StandardInput.Close(); if (!p.WaitForExit(500)) p.Kill(); } } catch { /* уже закрыт */ }
        p.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _gate.Dispose();
    }
}
