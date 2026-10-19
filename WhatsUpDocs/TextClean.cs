using System.Net;
using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public sealed record Section(string? Header, string Body);

public static class TextClean
{
    public const string Version = "v1";

    private const RegexOptions M = RegexOptions.Multiline | RegexOptions.Compiled;
    private static readonly Regex Header = new(@"^#+ .*$", M);
    private static readonly Regex HeaderLine = new(@"^#+ *(.*?)\s*$", M);
    private static readonly Regex TableLine = new(@"^\|.*$", M);
    private static readonly Regex Caption = new(@"^(Figure|Fig\.|Table)\s*\d[^\n]*$", M);
    private static readonly Regex ParenCitation = new(@"\s*\((?:[^()]*\d{4}[^()]*)\)", RegexOptions.Compiled);
    private static readonly Regex NumCitation = new(@"\s*\[\d+(?:[,–-]\s*\d+)*\]", RegexOptions.Compiled);
    private static readonly Regex HtmlComment = new(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BlankLines = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex SentenceBreak = new(@"(?<=[.!?])\s+(?=[A-Z(""'])", RegexOptions.Compiled);

    // Remove what real abstracts almost never contain: entities, image markers, tables, captions, citations
    public static string Clean(string text, bool keepHeaders)
    {
        string t = Decode(text.Replace("\r\n", "\n"));
        t = HtmlComment.Replace(t, "");
        t = TableLine.Replace(t, "");
        if (!keepHeaders) t = Header.Replace(t, "");
        t = Caption.Replace(t, "");
        t = ParenCitation.Replace(t, "");
        t = NumCitation.Replace(t, "");
        return BlankLines.Replace(t, "\n\n").Trim();
    }

    // Some papers are double-encoded (&amp;amp;), so decode until nothing changes
    private static string Decode(string t)
    {
        for (int i = 0; i < 3; i++)
        {
            string d = WebUtility.HtmlDecode(t);
            if (d == t) break;
            t = d;
        }
        return t;
    }

    public static int Words(string s) => Whitespace.Split(s.Trim()).Count(w => w.Length > 0);

    // Sentences of 6-90 words: shorter ones are usually heading/label fragments, longer ones are usually merged tables or lists
    public static List<string> Sentences(string cleaned)
    {
        string flat = Whitespace.Replace(cleaned, " ");
        return SentenceBreak.Split(flat).Select(s => s.Trim()).Where(s => Words(s) is >= 6 and <= 90).ToList();
    }

    public static List<Section> Sections(string cleanedWithHeaders)
    {
        var result = new List<Section>();
        string? header = null;
        int start = 0;
        foreach (Match m in HeaderLine.Matches(cleanedWithHeaders))
        {
            Add(cleanedWithHeaders[start..m.Index]);
            header = m.Groups[1].Value;
            start = m.Index + m.Length;
        }
        Add(cleanedWithHeaders[start..]);
        return result;

        void Add(string body)
        {
            if (!string.IsNullOrWhiteSpace(body) || header is not null) result.Add(new(header, body.Trim()));
        }
    }

    private static readonly Regex NotATitle = new(@"https?://|doi\.org|\b(19|20)\d{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ~9% of papers end with a short line without a period, often the paper title pushed to the end.
    // Lines with a URL/DOI/year are usually leftover references, not the title
    public static string? TrailingTitle(string rawText)
    {
        string cleaned = Clean(rawText, keepHeaders: true);
        string last = cleaned.Split("\n\n").LastOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim() ?? "";
        last = last.TrimStart('#', ' ');
        return last.Length > 0 && Words(last) <= 30 && !".!?:;)".Contains(last[^1]) && !NotATitle.IsMatch(last)
            ? last : null;
    }
}
