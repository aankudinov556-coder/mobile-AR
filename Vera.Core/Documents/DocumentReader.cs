using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using UglyToad.PdfPig;
using Vera.Core.Providers;

namespace Vera.Core.Documents;

/// <summary>Извлечение текста из файлов, которые прикладывают к сообщению: PDF, DOCX, текст и код.</summary>
public static partial class DocumentReader
{
    /// <summary>Сколько символов документа кладём прямо в сообщение (остальное — через read_document).</summary>
    public const int InlineLimit = 60_000;
    /// <summary>Размер части для read_document.</summary>
    public const int PartSize = 30_000;
    /// <summary>Максимальный размер файла.</summary>
    public const long MaxBytes = 50L * 1024 * 1024;

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".log", ".ini", ".cfg", ".toml",
        ".html", ".htm", ".css", ".js", ".ts", ".tsx", ".jsx", ".py", ".cs", ".java", ".kt", ".go", ".rs", ".c", ".cpp", ".h",
        ".hpp", ".php", ".rb", ".sh", ".ps1", ".bat", ".sql", ".lua", ".swift", ".dart", ".vue", ".srt", ".rtf",
    };

    public static readonly string[] ImageExt = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    /// <summary>Можно ли прочитать файл как документ (по расширению).</summary>
    public static bool IsSupported(string fileName)
    {
        var ext = Path.GetExtension(fileName);
        return ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase) || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase) || TextExt.Contains(ext);
    }

    public static bool IsImage(string fileName) => ImageExt.Contains(Path.GetExtension(fileName).ToLowerInvariant());

    /// <summary>Прочитать документ. Бросает понятное исключение, если формат не поддерживается.</summary>
    public static DocumentText Read(byte[] data, string fileName)
    {
        if (data.LongLength > MaxBytes) throw new ArgumentException($"Файл «{fileName}» слишком большой (больше 50 МБ).");
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var id = "d_" + Guid.NewGuid().ToString("N")[..8];
        return ext switch
        {
            ".pdf" => ReadPdf(id, data, fileName),
            ".docx" => new DocumentText(id, fileName, Clean(ReadDocx(data))),
            ".rtf" => new DocumentText(id, fileName, Clean(StripRtf(DecodeText(data)))),
            ".html" or ".htm" => new DocumentText(id, fileName, Tools.HtmlText.Extract(DecodeText(data)).Text),
            _ when TextExt.Contains(ext) => new DocumentText(id, fileName, DecodeText(data)),
            _ => throw new ArgumentException($"Формат «{ext}» пока не поддерживается. Подойдут PDF, DOCX, TXT/MD/CSV и файлы кода."),
        };
    }

    private static DocumentText ReadPdf(string id, byte[] data, string name)
    {
        using var doc = PdfDocument.Open(data);
        var sb = new StringBuilder();
        var pages = 0;
        foreach (var page in doc.GetPages())
        {
            pages++;
            sb.Append("\n[стр. ").Append(page.Number).Append("]\n");
            sb.Append(page.Text);
        }
        var text = Clean(sb.ToString());
        if (text.Replace("[стр.", "").Trim().Length < 20 * Math.Max(1, pages) / 4)
            text += "\n(В PDF почти нет текста — возможно, это скан. Пришли страницы картинками, и я посмотрю их глазами.)";
        return new DocumentText(id, name, text, pages);
    }

    /// <summary>Текст из word/document.xml: абзацы, переносы, табуляции, таблицы.</summary>
    private static string ReadDocx(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var entry = zip.GetEntry("word/document.xml") ?? throw new ArgumentException("Это не похоже на документ Word (.docx).");
        using var stream = entry.Open();
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var sb = new StringBuilder();
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                switch (reader.LocalName)
                {
                    case "t" when reader.NamespaceURI.Contains("wordprocessingml"):
                        sb.Append(reader.ReadElementContentAsString());
                        continue;
                    case "tab": sb.Append('\t'); break;
                    case "br" or "cr": sb.Append('\n'); break;
                    case "tc": sb.Append(" | "); break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName is "p" or "tr")
            {
                sb.Append('\n');
            }
        }
        return sb.ToString();
    }

    /// <summary>UTF-8 (с BOM или без) или Windows-1251 для старых русских файлов.</summary>
    public static string DecodeText(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE) return Encoding.Unicode.GetString(data, 2, data.Length - 2);
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2);
        try
        {
            var utf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);
            var s = utf8.GetString(data);
            return s.Length > 0 && s[0] == '\uFEFF' ? s[1..] : s;
        }
        catch (DecoderFallbackException)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(1251).GetString(data);
            }
            catch (Exception)
            {
                return Encoding.Latin1.GetString(data);
            }
        }
    }

    private static string StripRtf(string rtf)
    {
        var s = RtfControl().Replace(rtf, " ");
        return s.Replace("{", "").Replace("}", "");
    }

    private static string Clean(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\u00A0', ' ');
        s = Spaces().Replace(s, " ");
        s = Blank().Replace(s, "\n\n");
        return s.Trim();
    }

    /// <summary>Часть документа (1..N) для read_document.</summary>
    public static (string Text, int Part, int Parts) GetPart(string text, int part)
    {
        var parts = Math.Max(1, (int)Math.Ceiling(text.Length / (double)PartSize));
        part = Math.Clamp(part, 1, parts);
        var start = (part - 1) * PartSize;
        return (text.Substring(start, Math.Min(PartSize, text.Length - start)), part, parts);
    }

    /// <summary>Что положить в сообщение: весь текст или начало + подсказку про read_document.</summary>
    public static DocumentText ForMessage(DocumentText d)
    {
        if (d.Text.Length <= InlineLimit) return d;
        var parts = (int)Math.Ceiling(d.Text.Length / (double)PartSize);
        var head = d.Text[..InlineLimit];
        return d with
        {
            Text = head + $"\n\n[…документ длиннее: всего {d.Text.Length} символов, {parts} частей по {PartSize}. " +
                   $"Чтобы прочитать дальше, вызови read_document(id=\"{d.Id}\", part=N), N от 3 до {parts}.]",
        };
    }

    [GeneratedRegex(@"\\[a-zA-Z]+-?\d* ?|\\'[0-9a-fA-F]{2}|\\[^a-zA-Z]")] private static partial Regex RtfControl();
    [GeneratedRegex(@"[ \t]{2,}")] private static partial Regex Spaces();
    [GeneratedRegex(@"\n\s*\n\s*\n+")] private static partial Regex Blank();
}
