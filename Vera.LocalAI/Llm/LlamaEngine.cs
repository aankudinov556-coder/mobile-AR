using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;

namespace Vera.LocalAI.Llm;

/// <summary>
/// llama.cpp через LLamaSharp. Один контекст живёт между ходами: общий префикс промпта (системный промпт,
/// старые сообщения) остаётся в KV-кэше, и модель досчитывает только новое — на процессоре это экономит
/// десятки секунд на каждом шаге.
/// </summary>
public sealed class LlamaEngine : ILocalEngine
{
    private static readonly object NativeGate = new();
    private static bool _nativeConfigured;
    private static string _nativeBackend = "cpu";

    private readonly string _backendsDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private LLamaWeights? _weights;
    private LLamaContext? _ctx;
    private readonly List<LLamaToken> _cached = new();

    /// <param name="backendsDir">папка со скачанными ускорителями (внутри runtimes/win-x64/native/...)</param>
    public LlamaEngine(string backendsDir) => _backendsDir = backendsDir;

    public EngineLoadOptions? Loaded { get; private set; }
    public int ContextSize => (int)(_ctx?.ContextSize ?? 0);
    public string DeviceName => _nativeBackend switch { "vulkan" => "видеокарта (Vulkan)", "cuda12" => "видеокарта (CUDA)", _ => "процессор" };

    /// <summary>Последняя ошибка из журнала llama.cpp.</summary>
    public static string? LastNativeError { get; private set; }

    /// <summary>Ускоритель выбирается один раз за запуск программы (ограничение llama.cpp).</summary>
    public static bool BackendLocked => _nativeConfigured;

    /// <summary>Какой ускоритель использовался при первой загрузке (до перезапуска программы).</summary>
    public static string ActiveBackend => _nativeBackend;

    private void ConfigureNative(string backend)
    {
        lock (NativeGate)
        {
            if (_nativeConfigured) return;
            var native = Path.Combine(_backendsDir, "runtimes", "win-x64", "native");
            bool Installed(string v) => File.Exists(Path.Combine(native, v, "llama.dll"));
            var useVulkan = backend == "vulkan" || (backend == "auto" && Installed("vulkan"));
            var useCuda = backend == "cuda12" || (backend == "auto" && Installed("cuda12") && !useVulkan);
            if (backend == "cpu") useVulkan = useCuda = false;

            var cfg = NativeLibraryConfig.All;
            if (Directory.Exists(_backendsDir)) cfg.WithSearchDirectory(_backendsDir);
            cfg.WithCuda(useCuda).WithVulkan(useVulkan).WithAutoFallback(true)
               .WithLogCallback((level, message) =>
               {
                   // llama.cpp очень разговорчив — оставляем только ошибки (для текста исключений)
                   if (level == LLamaLogLevel.Error) LastNativeError = message.Trim();
               });
            _nativeBackend = useCuda ? "cuda12" : useVulkan ? "vulkan" : "cpu";
            _nativeConfigured = true;
        }
    }

    public async Task EnsureLoadedAsync(EngineLoadOptions o, IProgress<float>? progress, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Loaded is { } cur && cur with { Backend = o.Backend } == o with { Backend = o.Backend } && _ctx is not null) return;
            UnloadCore();
            if (!File.Exists(o.ModelPath)) throw new FileNotFoundException("Файл модели не найден", o.ModelPath);
            ConfigureNative(o.Backend);

            var gpu = o.GpuLayers >= 0 ? o.GpuLayers : (_nativeBackend == "cpu" ? 0 : 999);
            var threads = o.Threads > 0 ? o.Threads : Math.Max(2, Environment.ProcessorCount / 2);
            var p = new ModelParams(o.ModelPath)
            {
                ContextSize = (uint)Math.Clamp(o.ContextSize, 1024, 131072),
                GpuLayerCount = gpu,
                BatchSize = 512,
                Threads = threads,
            };
            try
            {
                _weights = await LLamaWeights.LoadFromFileAsync(p, ct, progress).ConfigureAwait(false);
                _ctx = _weights.CreateContext(p);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                UnloadCore();
                throw new InvalidOperationException(LoadError(e), e);
            }
            Loaded = o;
        }
        finally { _gate.Release(); }
    }

    private static string LoadError(Exception e) => e switch
    {
        LLama.Exceptions.LoadWeightsFailedException => "Не удалось открыть модель: файл повреждён, не докачан или эта версия не поддерживает такую архитектуру.",
        LLama.Exceptions.RuntimeError when e.Message.Contains("native", StringComparison.OrdinalIgnoreCase) =>
            "Не загрузились библиотеки llama.cpp. Выбери в настройках ускоритель «Процессор» и перезапусти Веру.",
        DllNotFoundException or BadImageFormatException =>
            "Не загрузились библиотеки llama.cpp. Выбери в настройках ускоритель «Процессор» и перезапусти Веру.",
        OutOfMemoryException or InsufficientExecutionStackException =>
            "Не хватило памяти. Возьми модель поменьше или уменьши размер контекста.",
        _ => "Не удалось загрузить модель: " + e.Message + (LastNativeError is { } n ? " (" + n + ")" : ""),
    };

    public int CountTokens(IReadOnlyList<ChatTurn> messages)
    {
        var ctx = _ctx ?? throw new InvalidOperationException("Модель не загружена");
        return ctx.Tokenize(Render(messages), addBos: true, special: true).Length;
    }

    private string Render(IReadOnlyList<ChatTurn> messages)
    {
        var w = _weights ?? throw new InvalidOperationException("Модель не загружена");
        try
        {
            var t = new LLamaTemplate(w, strict: true) { AddAssistant = true };
            foreach (var m in messages) t.Add(m.Role, m.Content);
            return Encoding.UTF8.GetString(t.Apply());
        }
        catch (LLama.Exceptions.TemplateNotFoundException)
        {
            return ChatMl(messages);
        }
    }

    /// <summary>Запасной шаблон, если в файле модели его нет.</summary>
    internal static string ChatMl(IReadOnlyList<ChatTurn> messages)
    {
        var sb = new StringBuilder();
        foreach (var m in messages) sb.Append("<|im_start|>").Append(m.Role).Append('\n').Append(m.Content).Append("<|im_end|>\n");
        return sb.Append("<|im_start|>assistant\n").ToString();
    }

    public async Task<GenerateStats> GenerateAsync(IReadOnlyList<ChatTurn> messages, GenerateOptions o, Action<string> onText, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ctx = _ctx ?? throw new InvalidOperationException("Модель не загружена");
            var tokens = ctx.Tokenize(Render(messages), addBos: true, special: true);
            var room = (int)ctx.ContextSize - tokens.Length;
            if (room < 64) throw new PromptTooLongException(tokens.Length, (int)ctx.ContextSize);
            var maxTokens = Math.Min(o.MaxTokens, room - 8);

            // общий префикс с тем, что уже лежит в KV-кэше
            var common = 0;
            while (common < _cached.Count && common < tokens.Length && _cached[common] == tokens[common]) common++;
            if (common == tokens.Length) common = Math.Max(0, common - 1); // хотя бы один токен прогнать, чтобы получить логиты
            ctx.NativeHandle.MemorySequenceRemove(LLamaSeqId.Zero, common, -1);
            _cached.RemoveRange(common, _cached.Count - common);
            var cachedTokens = common;

            var batch = new LLamaBatch();
            var batchSize = (int)ctx.BatchSize;
            for (var i = common; i < tokens.Length; i++)
            {
                batch.Add(tokens[i], i, LLamaSeqId.Zero, i == tokens.Length - 1);
                if (batch.TokenCount >= batchSize || i == tokens.Length - 1)
                {
                    ct.ThrowIfCancellationRequested();
                    await Decode(ctx, batch, ct).ConfigureAwait(false);
                    for (var k = i - batch.TokenCount + 1; k <= i; k++) _cached.Add(tokens[k]);
                    if (i < tokens.Length - 1) batch.Clear();
                }
            }

            using var sampler = new DefaultSamplingPipeline
            {
                Temperature = o.Temperature,
                TopP = o.TopP,
                RepeatPenalty = o.RepeatPenalty,
            };
            var decoder = new StreamingTokenDecoder(ctx) { DecodeSpecialTokens = false };
            var stops = new StopMatcher(o.Stops);
            var pos = tokens.Length;
            var logits = batch.TokenCount - 1;
            var produced = 0;
            var hitMax = true;
            for (; produced < maxTokens; produced++)
            {
                ct.ThrowIfCancellationRequested();
                var tok = sampler.Sample(ctx.NativeHandle, logits);
                if (tok.IsEndOfGeneration(ctx.Vocab)) { hitMax = false; break; }
                decoder.Add(tok);
                var piece = decoder.Read();
                var (emit, stop) = stops.Push(piece);
                if (emit.Length > 0) onText(emit);

                if (stop) { hitMax = false; produced++; break; }
                batch.Clear();
                batch.Add(tok, pos++, LLamaSeqId.Zero, true);
                logits = 0;
                await Decode(ctx, batch, ct).ConfigureAwait(false);
                _cached.Add(tok); // в кэше только то, что реально посчитано
            }
            var rest = stops.Flush();
            if (rest.Length > 0) onText(rest);
            return new GenerateStats(tokens.Length, cachedTokens, produced, hitMax);
        }
        catch (OperationCanceledException)
        {
            // кэш мог остаться недосчитанным — в следующий раз начнём с чистого
            ResetCache();
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task Decode(LLamaContext ctx, LLamaBatch batch, CancellationToken ct)
    {
        var r = await ctx.DecodeAsync(batch, ct).ConfigureAwait(false);
        if (r == DecodeResult.Ok) return;
        ResetCache();
        throw new InvalidOperationException(r == DecodeResult.NoKvSlot
            ? "Контекст модели переполнен — начни новый разговор или увеличь размер контекста."
            : "Ошибка модели при обработке текста (" + r + ").");
    }

    private void ResetCache()
    {
        _cached.Clear();
        try { _ctx?.NativeHandle.MemoryClear(true); } catch { /* контекст уже освобождён */ }
    }

    public void Unload()
    {
        _gate.Wait();
        try { UnloadCore(); } finally { _gate.Release(); }
    }

    private void UnloadCore()
    {
        _cached.Clear();
        _ctx?.Dispose();
        _ctx = null;
        _weights?.Dispose();
        _weights = null;
        Loaded = null;
    }

    public void Dispose() => Unload();
}

/// <summary>Промпт не помещается в контекст модели.</summary>
public sealed class PromptTooLongException(int tokens, int context)
    : Exception($"Промпт ({tokens} токенов) не помещается в контекст модели ({context}).")
{
    public int Tokens { get; } = tokens;
    public int Context { get; } = context;
}

/// <summary>
/// Ищет стоп-строки в потоке текста. Хвост, который может оказаться началом стоп-строки, придерживается,
/// поэтому сама стоп-строка никогда не уходит в UI.
/// </summary>
public sealed class StopMatcher(IReadOnlyList<string> stops)
{
    private readonly StringBuilder _hold = new();
    private bool _stopped;

    public (string Emit, bool Stop) Push(string piece)
    {
        if (_stopped || piece.Length == 0) return ("", _stopped);
        _hold.Append(piece);
        var s = _hold.ToString();
        foreach (var stop in stops)
        {
            var i = s.IndexOf(stop, StringComparison.Ordinal);
            if (i < 0) continue;
            _stopped = true;
            _hold.Clear();
            return (s[..i], true);
        }
        var keep = 0;
        foreach (var stop in stops)
            for (var k = Math.Min(stop.Length - 1, s.Length); k > keep; k--)
                if (s.EndsWith(stop[..k], StringComparison.Ordinal)) { keep = k; break; }
        _hold.Clear();
        _hold.Append(s[^keep..]);
        return (s[..^keep], false);
    }

    public string Flush()
    {
        var s = _stopped ? "" : _hold.ToString();
        _hold.Clear();
        return s;
    }
}
