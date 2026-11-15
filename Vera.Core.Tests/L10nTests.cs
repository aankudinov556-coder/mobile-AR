using System.Text.RegularExpressions;
using Xunit;

namespace Vera.Core.Tests;

[CollectionDefinition("L10n", DisableParallelization = true)]
public sealed class L10nCollection { }

[Collection("L10n")]
public class L10nTests
{
    private static readonly Regex Hole = new(@"\{[^}]*\}");

    [Fact]
    public void DictionaryLoadsAndKeepsPlaceholders()
    {
        Assert.True(L10n.Dictionary.Count > 500);
        foreach (var (ru, en) in L10n.Dictionary)
        {
            Assert.False(string.IsNullOrWhiteSpace(en), ru);
            Assert.Equal(Hole.Matches(ru).Select(m => m.Value).Order(), Hole.Matches(en).Select(m => m.Value).Order());
        }
    }

    [Fact]
    public void TranslatesOnlyInEnglish()
    {
        var was = L10n.Lang;
        try
        {
            L10n.Lang = "ru";
            Assert.Equal("Настройки", L10n.T("Настройки"));
            Assert.Equal("Дела · 3", L10n.F("Дела · {0}", 3));
            L10n.Lang = "en";
            Assert.Equal("Settings", L10n.T("Настройки"));
            Assert.Equal("To-do · 3", L10n.F("Дела · {0}", 3));
            Assert.Equal("нет такой строки", L10n.T("нет такой строки"));
            Assert.Equal("", L10n.T(null));
        }
        finally
        {
            L10n.Lang = was;
        }
    }

    [Fact]
    public void ExistingInstallStaysRussian()
    {
        var old = new MemStorage();
        old.WriteText("settings.json", "{\"userName\":\"Андрей\"}");
        Assert.Equal("ru", new SettingsService(old).Current.Language);

        var fresh = new SettingsService(new MemStorage());
        Assert.Equal(L10n.SystemDefault(), fresh.Current.Language);

        var chosen = new MemStorage();
        chosen.WriteText("settings.json", "{\"language\":\"en\"}");
        Assert.Equal("en", new SettingsService(chosen).Current.Language);
    }

    /// <summary>Каждая строка, которую код явно переводит (L10n.T/F("…")), есть в словаре.</summary>
    [Fact]
    public void EveryExplicitKeyHasTranslation()
    {
        var root = AppContext.BaseDirectory;
        while (root is not null && !Directory.Exists(Path.Combine(root, "Vera.Core"))) root = Path.GetDirectoryName(root);
        Assert.NotNull(root);
        var call = new Regex(@"L10n\.[TF]\(\s*""((?:[^""\\]|\\.)*)""");
        var missing = new List<string>();
        foreach (var dir in new[] { "Vera.Core", "Vera.Desktop", "Vera.Mobile", "Vera.LocalAI" })
        {
            var path = Path.Combine(root!, dir);
            if (!Directory.Exists(path)) continue;
            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
                foreach (Match m in call.Matches(File.ReadAllText(file)))
                {
                    var key = Regex.Unescape(m.Groups[1].Value);
                    if (Regex.IsMatch(key, "[А-Яа-яЁё]") && !L10n.Dictionary.ContainsKey(key))
                        missing.Add($"{Path.GetFileName(file)}: {key}");
                }
            }
        }
        Assert.True(missing.Count == 0, "Нет перевода:\n" + string.Join("\n", missing.Distinct()));
    }
}
