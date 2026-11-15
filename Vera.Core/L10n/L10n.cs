using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Vera.Core;

/// <summary>
/// Язык интерфейса. Тексты в коде пишутся по-русски, английский перевод — словарь en.json
/// (русская строка → английская), общий для ПК и телефона. Нет перевода — остаётся русский.
/// Строки с подстановками — через <see cref="F"/>: ключ с {0}, {1}…
/// </summary>
public static class L10n
{
    private static readonly Lazy<Dictionary<string, string>> En = new(Load);

    /// <summary>"ru" | "en". Меняется при запуске из настроек (полностью — после перезапуска).</summary>
    public static string Lang { get; set; } = "ru";

    public static bool English => Lang == "en";

    /// <summary>Язык по умолчанию для новой установки — по языку Windows / Android.</summary>
    public static string SystemDefault()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return two is "ru" or "uk" or "be" or "kk" ? "ru" : "en";
    }

    private static Dictionary<string, string> Load()
    {
        try
        {
            using var s = typeof(L10n).Assembly.GetManifestResourceStream("Vera.Core.L10n.en.json");
            if (s is null) return new();
            return JsonSerializer.Deserialize<Dictionary<string, string>>(s) ?? new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    /// <summary>Перевести строку интерфейса (ключ — русский текст).</summary>
    public static string T(string? ru)
    {
        if (string.IsNullOrEmpty(ru) || !English) return ru ?? "";
        return En.Value.TryGetValue(ru, out var en) ? en : ru;
    }

    /// <summary>Перевести шаблон и подставить значения: F("Скачано: {0}", name).</summary>
    public static string F(string ru, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(ru), args);

    /// <summary>Сколько строк в словаре (для тестов).</summary>
    public static IReadOnlyDictionary<string, string> Dictionary => En.Value;
}
