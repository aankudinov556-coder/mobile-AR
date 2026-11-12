using System.Text;
using System.Text.RegularExpressions;

namespace Vera.Core.Markdown;

public enum MdBlockKind { Paragraph, Heading, Code, Bullet, Numbered, Quote, Rule }

public enum MdInlineKind { Text, Bold, Italic, Code, Link, Strike }

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

/// <summary>Блок разметки. Для Code — Language и Code; для списков — Items (каждый пункт — набор inline).</summary>
public sealed class MdBlock
{
    public MdBlockKind Kind { get; init; }
    public int Level { get; init; }
    public List<MdInline> Inlines { get; init; } = new();
    public List<List<MdInline>> Items { get; init; } = new();
    public string? Language { get; init; }
    public string? Code { get; init; }
    public int Start { get; init; } = 1;
}

/// <summary>
/// Маленький парсер Markdown в простое дерево — обе платформы рисуют его своими контролами.
/// Незакрытый блок кода (во время стриминга) считается закрытым.
/// </summary>
public static partial class MarkdownLite
{
    public static List<MdBlock> Parse(string? md)
    {
        var text = (md ?? "").Replace("\r\n", "\n");
        var blocks = new List<MdBlock>();
        var lines = text.Split('\n');
        var i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];
            var fence = FenceRe().Match(line);
            if (fence.Success)
            {
                var lang = fence.Groups[1].Value;
                var code = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```")) code.Append(lines[i++]).Append('\n');
                i++; // закрывающая ``` (или конец)
                blocks.Add(new MdBlock { Kind = MdBlockKind.Code, Language = lang, Code = code.ToString().TrimEnd('\n') });
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }
            var h = HeadingRe().Match(line);
            if (h.Success)
            {
                blocks.Add(new MdBlock { Kind = MdBlockKind.Heading, Level = h.Groups[1].Value.Length, Inlines = ParseInline(h.Groups[2].Value) });
                i++;
                continue;
            }
            if (RuleRe().IsMatch(line)) { blocks.Add(new MdBlock { Kind = MdBlockKind.Rule }); i++; continue; }
            if (QuoteRe().IsMatch(line))
            {
                var q = new List<string>();
                while (i < lines.Length && QuoteRe().IsMatch(lines[i])) q.Add(QuoteRe().Replace(lines[i++], ""));
                blocks.Add(new MdBlock { Kind = MdBlockKind.Quote, Inlines = ParseInline(string.Join("\n", q)) });
                continue;
            }
            if (BulletRe().IsMatch(line))
            {
                var items = new List<List<MdInline>>();
                while (i < lines.Length && BulletRe().IsMatch(lines[i])) items.Add(ParseInline(BulletRe().Replace(lines[i++], "")));
                blocks.Add(new MdBlock { Kind = MdBlockKind.Bullet, Items = items });
                continue;
            }
            var num = NumRe().Match(line);
            if (num.Success)
            {
                var start = int.TryParse(num.Groups[1].Value, out var n) ? n : 1;
                var items = new List<List<MdInline>>();
                while (i < lines.Length && NumRe().IsMatch(lines[i])) items.Add(ParseInline(NumRe().Replace(lines[i++], "")));
                blocks.Add(new MdBlock { Kind = MdBlockKind.Numbered, Items = items, Start = start });
                continue;
            }
            var para = new List<string>();
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) && !FenceRe().IsMatch(lines[i]) && !HeadingRe().IsMatch(lines[i])
                   && !BulletRe().IsMatch(lines[i]) && !NumRe().IsMatch(lines[i]) && !QuoteRe().IsMatch(lines[i]))
                para.Add(lines[i++]);
            if (para.Count == 0) { para.Add(lines[i++]); }
            blocks.Add(new MdBlock { Kind = MdBlockKind.Paragraph, Inlines = ParseInline(string.Join("\n", para)) });
        }
        return blocks;
    }

    public static List<MdInline> ParseInline(string s)
    {
        var result = new List<MdInline>();
        var pos = 0;
        foreach (Match m in InlineRe().Matches(s))
        {
            if (m.Index > pos) result.Add(new MdInline(MdInlineKind.Text, s[pos..m.Index]));
            if (m.Groups["code"].Success) result.Add(new MdInline(MdInlineKind.Code, m.Groups["code"].Value));
            else if (m.Groups["lt"].Success) result.Add(new MdInline(MdInlineKind.Link, m.Groups["lt"].Value, m.Groups["lu"].Value));
            else if (m.Groups["url"].Success) result.Add(new MdInline(MdInlineKind.Link, m.Groups["url"].Value, m.Groups["url"].Value));
            else if (m.Groups["b"].Success) result.Add(new MdInline(MdInlineKind.Bold, m.Groups["b"].Value));
            else if (m.Groups["b2"].Success) result.Add(new MdInline(MdInlineKind.Bold, m.Groups["b2"].Value));
            else if (m.Groups["s"].Success) result.Add(new MdInline(MdInlineKind.Strike, m.Groups["s"].Value));
            else if (m.Groups["i"].Success) result.Add(new MdInline(MdInlineKind.Italic, m.Groups["i"].Value));
            pos = m.Index + m.Length;
        }
        if (pos < s.Length) result.Add(new MdInline(MdInlineKind.Text, s[pos..]));
        return result;
    }

    /// <summary>Текст без разметки (для пузыря и озвучки).</summary>
    public static string ToPlain(string? md)
    {
        var sb = new StringBuilder();
        foreach (var b in Parse(md))
        {
            if (sb.Length > 0) sb.Append('\n');
            switch (b.Kind)
            {
                case MdBlockKind.Code: sb.Append("[код]"); break;
                case MdBlockKind.Rule: break;
                case MdBlockKind.Bullet:
                case MdBlockKind.Numbered:
                    sb.Append(string.Join("\n", b.Items.Select(it => "• " + string.Concat(it.Select(x => x.Text))))); break;
                default: sb.Append(string.Concat(b.Inlines.Select(x => x.Text))); break;
            }
        }
        return sb.ToString();
    }

    [GeneratedRegex(@"^\s*```([\w+#.-]*)")] private static partial Regex FenceRe();
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")] private static partial Regex HeadingRe();
    [GeneratedRegex(@"^\s*([-*_])\s*\1\s*\1[\s\-*_]*$")] private static partial Regex RuleRe();
    [GeneratedRegex(@"^\s*>\s?")] private static partial Regex QuoteRe();
    [GeneratedRegex(@"^\s*[-*•+]\s+")] private static partial Regex BulletRe();
    [GeneratedRegex(@"^\s*(\d+)[.)]\s+")] private static partial Regex NumRe();
    [GeneratedRegex(@"`(?<code>[^`\n]+)`|\[(?<lt>[^\]]+)\]\((?<lu>https?://[^\s)]+)\)|(?<url>https?://[^\s<)]+[^\s<).,!?;:'""])|\*\*(?<b>[^*\n]+)\*\*|__(?<b2>[^_\n]+)__|~~(?<s>[^~\n]+)~~|(?<![*\w])\*(?<i>[^*\n]+)\*(?!\w)")]
    private static partial Regex InlineRe();
}
