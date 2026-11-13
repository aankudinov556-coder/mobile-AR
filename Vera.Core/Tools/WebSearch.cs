using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Vera.Core.Tools;

public sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>
/// Веб-поиск для любой модели: Tavily / Brave (по ключу), DuckDuckGo (без ключа), Википедия (запасной вариант).
/// </summary>
public static partial class WebSearch
{
    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36";

    /// <summary>engine: auto | duckduckgo | wikipedia | tavily | brave</summary>
    public static async Task<(List<SearchHit> Hits, string Engine)> SearchAsync(HttpClient http, string query, string engine,
        Func<string, string> getKey, int count = 5, CancellationToken ct = default)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) throw new ArgumentException("Пустой поисковый запрос");
        var order = engine switch
        {
            "tavily" => new[] { "tavily", "duckduckgo", "wikipedia" },
            "brave" => new[] { "brave", "duckduckgo", "wikipedia" },
            "duckduckgo" => new[] { "duckduckgo", "wikipedia" },
            "wikipedia" => new[] { "wikipedia" },
            _ => new[] { "tavily", "brave", "duckduckgo", "wikipedia" },
        };
        Exception? last = null;
        foreach (var e in order)
        {
            if (e == "tavily" && string.IsNullOrWhiteSpace(getKey("tavily"))) continue;
            if (e == "brave" && string.IsNullOrWhiteSpace(getKey("brave"))) continue;
            try
            {
                var hits = e switch
                {
                    "tavily" => await TavilyAsync(http, query, getKey("tavily"), count, ct),
                    "brave" => await BraveAsync(http, query, getKey("brave"), count, ct),
                    "duckduckgo" => await DuckDuckGoAsync(http, query, count, ct),
                    _ => await WikipediaAsync(http, query, count, ct),
                };
                if (hits.Count > 0) return (hits, e);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
            }
        }
        if (last is not null) throw new InvalidOperationException("Поиск не удался: " + last.Message, last);
        return (new List<SearchHit>(), order.Last());
    }

    private static async Task<List<SearchHit>> TavilyAsync(HttpClient http, string q, string key, int count, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.tavily.com/search");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Content = new StringContent(new JsonObject { ["query"] = q, ["max_results"] = count, ["search_depth"] = "basic" }.ToJsonString(),
            Encoding.UTF8, "application/json");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return (j?["results"]?.AsArray() ?? new JsonArray()).Where(r => r is not null)
            .Select(r => new SearchHit(S(r!["title"]), S(r["url"]), S(r["content"]))).ToList();
    }

    private static async Task<List<SearchHit>> BraveAsync(HttpClient http, string q, string key, int count, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.search.brave.com/res/v1/web/search?count={count}&q={Uri.EscapeDataString(q)}");
        req.Headers.Add("X-Subscription-Token", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return (j?["web"]?["results"]?.AsArray() ?? new JsonArray()).Where(r => r is not null)
            .Select(r => new SearchHit(S(r!["title"]), S(r["url"]), HtmlText.Plain(S(r["description"])))).ToList();
    }

    private static async Task<List<SearchHit>> DuckDuckGoAsync(HttpClient http, string q, int count, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://html.duckduckgo.com/html/");
        req.Headers.UserAgent.ParseAdd(Ua);
        req.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = q, ["kl"] = "ru-ru" });
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        return ParseDuckDuckGo(await res.Content.ReadAsStringAsync(ct), count);
    }

    /// <summary>Разбор HTML-выдачи DuckDuckGo (html.duckduckgo.com).</summary>
    public static List<SearchHit> ParseDuckDuckGo(string html, int count = 5)
    {
        var hits = new List<SearchHit>();
        var links = DdgLink().Matches(html);
        var snippets = DdgSnippet().Matches(html);
        for (var i = 0; i < links.Count && hits.Count < count; i++)
        {
            var href = WebUtility.HtmlDecode(links[i].Groups["href"].Value);
            var url = DdgTarget(href);
            if (url is null || url.Contains("duckduckgo.com/y.js")) continue; // реклама
            var title = HtmlText.Plain(links[i].Groups["title"].Value);
            var snippet = i < snippets.Count ? HtmlText.Plain(snippets[i].Groups["s"].Value) : "";
            hits.Add(new SearchHit(title, url, snippet));
        }
        return hits;
    }

    private static string? DdgTarget(string href)
    {
        if (href.StartsWith("//")) href = "https:" + href;
        if (!Uri.TryCreate(href, UriKind.Absolute, out var u)) return null;
        if (u.Host.EndsWith("duckduckgo.com") && u.AbsolutePath.StartsWith("/l/"))
        {
            foreach (var part in u.Query.TrimStart('?').Split('&'))
                if (part.StartsWith("uddg=")) return Uri.UnescapeDataString(part[5..]);
            return null;
        }
        return u.ToString();
    }

    private static async Task<List<SearchHit>> WikipediaAsync(HttpClient http, string q, int count, CancellationToken ct)
    {
        var lang = q.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') ? "ru" : "en";
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://{lang}.wikipedia.org/w/api.php?action=query&list=search&format=json&utf8=1&srlimit={count}&srsearch={Uri.EscapeDataString(q)}");
        req.Headers.UserAgent.ParseAdd("VeraAssistant/2.2 (desktop assistant)");
        using var res = await http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        return (j?["query"]?["search"]?.AsArray() ?? new JsonArray()).Where(r => r is not null)
            .Select(r =>
            {
                var title = S(r!["title"]);
                return new SearchHit(title, $"https://{lang}.wikipedia.org/wiki/{Uri.EscapeDataString(title.Replace(' ', '_'))}", HtmlText.Plain(S(r["snippet"])));
            }).ToList();
    }

    /// <summary>Скачать страницу и вытащить основной текст (не больше maxChars).</summary>
    public static async Task<(string Title, string Text, bool Cut)> ReadPageAsync(HttpClient http, string url, int maxChars = 12_000, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            throw new ArgumentException("Нужна ссылка http(s)");
        using var req = new HttpRequestMessage(HttpMethod.Get, u);
        req.Headers.UserAgent.ParseAdd(Ua);
        req.Headers.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.7");
        using var res = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        res.EnsureSuccessStatusCode();
        var type = res.Content.Headers.ContentType?.MediaType ?? "";
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buf = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(chunk, ct)) > 0 && buf.Length < 3_000_000) buf.Write(chunk, 0, n);
        var bytes = buf.ToArray();
        if (type.Contains("pdf"))
        {
            var doc = Documents.DocumentReader.Read(bytes, "page.pdf");
            return Cut(u.Host, doc.Text, maxChars);
        }
        var html = Documents.DocumentReader.DecodeText(bytes);
        if (!type.Contains("html") && !html.TrimStart().StartsWith('<')) return Cut(u.Host, html, maxChars);
        var (title, text) = HtmlText.Extract(html);
        return Cut(string.IsNullOrWhiteSpace(title) ? u.Host : title, text, maxChars);
    }

    private static (string, string, bool) Cut(string title, string text, int max) =>
        text.Length > max ? (title, text[..max], true) : (title, text, false);

    private static string S(JsonNode? n) => n?.GetValue<string>() ?? "";

    [GeneratedRegex(@"<a[^>]+class=""result__a""[^>]+href=""(?<href>[^""]+)""[^>]*>(?<title>[\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex DdgLink();
    [GeneratedRegex(@"class=""result__snippet""[^>]*>(?<s>[\s\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex DdgSnippet();
}

/// <summary>HTML → читаемый текст: убирает скрипты, меню, подвал; предпочитает article/main.</summary>
public static partial class HtmlText
{
    public static (string Title, string Text) Extract(string html)
    {
        var title = Plain(TitleRe().Match(html).Groups[1].Value);
        var body = html;
        foreach (var re in new[] { DropBlocks(), Comments() }) body = re.Replace(body, " ");
        var main = ArticleRe().Match(body);
        if (!main.Success) main = MainRe().Match(body);
        if (main.Success && Plain(main.Value).Length > 400) body = main.Value;
        body = BlockEnd().Replace(body, "\n");
        body = Br().Replace(body, "\n");
        var text = Plain(body, keepNewlines: true);
        return (title, text);
    }

    public static string Plain(string html, bool keepNewlines = false)
    {
        var s = Tags().Replace(html ?? "", " ");
        s = WebUtility.HtmlDecode(s).Replace(' ', ' ');
        if (!keepNewlines) return Ws().Replace(s, " ").Trim();
        s = InlineWs().Replace(s, " ");
        s = string.Join("\n", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        return s.Trim();
    }

    [GeneratedRegex(@"<title[^>]*>([\s\S]*?)</title>", RegexOptions.IgnoreCase)] private static partial Regex TitleRe();
    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|aside|form|iframe|template|button|select)\b[\s\S]*?</\1>", RegexOptions.IgnoreCase)] private static partial Regex DropBlocks();
    [GeneratedRegex(@"<!--[\s\S]*?-->")] private static partial Regex Comments();
    [GeneratedRegex(@"<article\b[\s\S]*?</article>", RegexOptions.IgnoreCase)] private static partial Regex ArticleRe();
    [GeneratedRegex(@"<main\b[\s\S]*?</main>", RegexOptions.IgnoreCase)] private static partial Regex MainRe();
    [GeneratedRegex(@"</(p|div|h[1-6]|li|tr|section|blockquote|pre|table|ul|ol|dt|dd)>", RegexOptions.IgnoreCase)] private static partial Regex BlockEnd();
    [GeneratedRegex(@"<br\s*/?>", RegexOptions.IgnoreCase)] private static partial Regex Br();
    [GeneratedRegex(@"<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Ws();
    [GeneratedRegex(@"[ \t\r\f\v]+")] private static partial Regex InlineWs();
}
