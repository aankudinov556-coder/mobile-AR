namespace Vera.LocalAI;

/// <summary>Что за файл: языковая модель, модель распознавания, ускоритель.</summary>
public enum AssetKind { Llm, Vosk, Whisper, Backend }

/// <summary>
/// Скачиваемый файл. <see cref="Files"/> — несколько частей (например, модель + словарь).
/// <see cref="Extract"/>: zip/nupkg распаковывается в папку <see cref="Folder"/> (берутся файлы из <see cref="ExtractPrefix"/>).
/// </summary>
public sealed record AssetInfo(
    string Id,
    AssetKind Kind,
    string Title,
    string Note,
    long SizeMB,
    IReadOnlyList<AssetFile> Files,
    bool Extract = false,
    string? ExtractPrefix = null,
    string? Folder = null)
{
    /// <summary>Для LLM: минимум оперативной памяти, ГБ.</summary>
    public int MinRamGB { get; init; }
    /// <summary>Для LLM: насколько хорошо модель вызывает инструменты (0–3).</summary>
    public int ToolsQuality { get; init; }
    /// <summary>Для LLM: насколько хорошо говорит по-русски (0–3).</summary>
    public int RussianQuality { get; init; }
}

public sealed record AssetFile(string Url, string FileName);

/// <summary>
/// Каталог рекомендованных моделей. Все файлы скачиваются по требованию из официальных репозиториев
/// (Hugging Face, alphacephei.com, GitHub, nuget.org) и лежат в папке данных: %LOCALAPPDATA%\VeraAssistant\models.
/// </summary>
public static class ModelCatalog
{
    private const string Hf = "https://huggingface.co/";

    public static readonly IReadOnlyList<AssetInfo> Llms = new[]
    {
        new AssetInfo("qwen2.5-1.5b", AssetKind.Llm, "Qwen2.5 1.5B — лёгкая",
            "Быстрая даже на слабом ноутбуке. Отвечает проще, иногда путается в командах.", 1120,
            new[] { new AssetFile(Hf + "Qwen/Qwen2.5-1.5B-Instruct-GGUF/resolve/main/qwen2.5-1.5b-instruct-q4_k_m.gguf", "qwen2.5-1.5b-instruct-q4_k_m.gguf") })
            { MinRamGB = 4, ToolsQuality = 1, RussianQuality = 1 },
        new AssetInfo("qwen2.5-3b", AssetKind.Llm, "Qwen2.5 3B — сбалансированная",
            "Хороший выбор для 8 ГБ памяти. Нормально ставит напоминания и запускает программы.", 2100,
            new[] { new AssetFile(Hf + "Qwen/Qwen2.5-3B-Instruct-GGUF/resolve/main/qwen2.5-3b-instruct-q4_k_m.gguf", "qwen2.5-3b-instruct-q4_k_m.gguf") })
            { MinRamGB = 8, ToolsQuality = 2, RussianQuality = 2 },
        new AssetInfo("qwen3-4b", AssetKind.Llm, "Qwen3 4B Instruct 2507",
            "Новее и умнее 3B, хорошо вызывает инструменты. Нужен процессор поновее или видеокарта.", 2500,
            new[] { new AssetFile(Hf + "unsloth/Qwen3-4B-Instruct-2507-GGUF/resolve/main/Qwen3-4B-Instruct-2507-Q4_K_M.gguf", "Qwen3-4B-Instruct-2507-Q4_K_M.gguf") })
            { MinRamGB = 8, ToolsQuality = 3, RussianQuality = 2 },
        new AssetInfo("gemma3-4b", AssetKind.Llm, "Gemma 3 4B",
            "Лучше всех из маленьких говорит по-русски, характер держит хорошо. Команды — средне.", 2490,
            new[] { new AssetFile(Hf + "bartowski/google_gemma-3-4b-it-GGUF/resolve/main/google_gemma-3-4b-it-Q4_K_M.gguf", "google_gemma-3-4b-it-Q4_K_M.gguf") })
            { MinRamGB = 8, ToolsQuality = 2, RussianQuality = 3 },
        new AssetInfo("qwen2.5-7b", AssetKind.Llm, "Qwen2.5 7B — умная",
            "Близко к облачным моделям попроще. Нужно 16 ГБ памяти, лучше с видеокартой.", 4680,
            new[] { new AssetFile(Hf + "bartowski/Qwen2.5-7B-Instruct-GGUF/resolve/main/Qwen2.5-7B-Instruct-Q4_K_M.gguf", "Qwen2.5-7B-Instruct-Q4_K_M.gguf") })
            { MinRamGB = 16, ToolsQuality = 3, RussianQuality = 3 },
    };

    public static readonly IReadOnlyList<AssetInfo> Voice = new[]
    {
        new AssetInfo("vosk-small-ru", AssetKind.Vosk, "Vosk — слово «Вера» офлайн",
            "Маленькая модель (45 МБ): слушает имя постоянно и почти не грузит процессор.", 45,
            new[] { new AssetFile("https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip", "vosk-model-small-ru-0.22.zip") },
            Extract: true, ExtractPrefix: "vosk-model-small-ru-0.22/", Folder: "vosk-small-ru"),

        new AssetInfo("whisper-base", AssetKind.Whisper, "Whisper base",
            "Быстро, но ошибается в словах. Для слабых ПК.", 148,
            new[] { new AssetFile(Hf + "ggerganov/whisper.cpp/resolve/main/ggml-base.bin", "ggml-base.bin") }),
        new AssetInfo("whisper-small", AssetKind.Whisper, "Whisper small",
            "Хорошо понимает русский, 1–3 с на фразу на обычном процессоре.", 488,
            new[] { new AssetFile(Hf + "ggerganov/whisper.cpp/resolve/main/ggml-small.bin", "ggml-small.bin") }),
        new AssetInfo("whisper-turbo", AssetKind.Whisper, "Whisper large-v3 turbo (q5)",
            "Почти как облако. Медленно без видеокарты.", 574,
            new[] { new AssetFile(Hf + "ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin", "ggml-large-v3-turbo-q5_0.bin") }),
        new AssetInfo("whisper-medium", AssetKind.Whisper, "Whisper medium",
            "Точнее small, но в 3 раза медленнее.", 1530,
            new[] { new AssetFile(Hf + "ggerganov/whisper.cpp/resolve/main/ggml-medium.bin", "ggml-medium.bin") }),

    };

    /// <summary>Ускорение на видеокарте — нативные библиотеки llama.cpp из пакетов LLamaSharp на nuget.org.</summary>
    public static readonly IReadOnlyList<AssetInfo> Backends = new[]
    {
        new AssetInfo("backend-vulkan", AssetKind.Backend, "Видеокарта (Vulkan)",
            "Любая видеокарта NVIDIA / AMD / Intel с обычным драйвером. Ускоряет ответы в 3–10 раз.", 55,
            new[] { new AssetFile("https://api.nuget.org/v3-flatcontainer/llamasharp.backend.vulkan.windows/0.30.0/llamasharp.backend.vulkan.windows.0.30.0.nupkg", "vulkan.nupkg") },
            Extract: true, ExtractPrefix: "LLamaSharpRuntimes/", Folder: "backends/runtimes"),
        new AssetInfo("backend-cuda12", AssetKind.Backend, "NVIDIA CUDA 12 (эксперимент)",
            "Самое быстрое для NVIDIA, но нужен установленный CUDA Toolkit 12 и много места.", 540,
            new[] { new AssetFile("https://api.nuget.org/v3-flatcontainer/llamasharp.backend.cuda12.windows/0.30.0/llamasharp.backend.cuda12.windows.0.30.0.nupkg", "cuda12.nupkg") },
            Extract: true, ExtractPrefix: "LLamaSharpRuntimes/", Folder: "backends/runtimes"),
    };

    public static IEnumerable<AssetInfo> All => Llms.Concat(Voice).Concat(Backends);

    public static AssetInfo? Get(string id) => All.FirstOrDefault(a => a.Id == id);

    public static AssetInfo? WhisperFor(string size) => Get("whisper-" + (string.IsNullOrWhiteSpace(size) ? "small" : size));
}
