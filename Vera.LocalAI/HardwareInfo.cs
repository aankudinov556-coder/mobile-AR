
namespace Vera.LocalAI;

/// <summary>Что есть у компьютера — для рекомендаций модели и ускорителя.</summary>
public sealed record HardwareInfo(double RamGB, int Cores, bool Avx2, bool Avx512, bool HasVulkan, bool HasNvidia)
{
    public static HardwareInfo Detect()
    {
        var ram = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024 / 1024;
        var sys = Environment.SystemDirectory;
        bool Has(string dll) => OperatingSystem.IsWindows() && File.Exists(Path.Combine(sys, dll));
        return new HardwareInfo(
            Math.Round(ram, 1),
            Environment.ProcessorCount,
            System.Runtime.Intrinsics.X86.Avx2.IsSupported,
            System.Runtime.Intrinsics.X86.Avx512F.IsSupported,
            Has("vulkan-1.dll"),
            Has("nvcuda.dll"));
    }

    /// <summary>Подходящая модель по памяти: 1.5B до 8 ГБ, 4B до 16 ГБ, дальше 7B.</summary>
    public string RecommendedLlm() => RamGB switch
    {
        < 7.5 => "qwen2.5-1.5b",
        < 15 => "qwen3-4b",
        _ => HasVulkan ? "qwen2.5-7b" : "qwen3-4b",
    };

    /// <summary>Whisper: small, если процессор не совсем старый.</summary>
    public string RecommendedWhisper() => Cores >= 6 && Avx2 ? "small" : "base";

    /// <summary>Ускоритель: Vulkan, если драйвер есть.</summary>
    public string RecommendedBackend() => HasVulkan ? "vulkan" : "cpu";

    public string Describe()
    {
        var gpu = HasNvidia ? "видеокарта NVIDIA" : HasVulkan ? "видеокарта с Vulkan" : "видеокарта не найдена";
        return $"{RamGB:0.#} ГБ памяти · {Cores} потоков · {(Avx2 ? "AVX2" : "без AVX2")} · {gpu}";
    }
}
