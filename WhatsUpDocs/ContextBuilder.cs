using System.Text;
using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public enum ContextMode { Sections, Full, Numbered }

public sealed record ContextOptions(
    ContextMode Mode = ContextMode.Sections,
    int IntroTokens = 2500, int EndTokens = 1500, int MiddleTokens = 500, int FullTokens = 30000);

public sealed record ContextResult(string Text, IReadOnlyList<string> Sentences, int ApproxTokens);

// Phần bài đưa cho LLM. Câu giống abstract tập trung ở section đầu và cuối (EDA: mật độ gấp ~1,5 lần phần giữa)
public static class ContextBuilder
{
    // Ước lượng thô ~4 ký tự/token tiếng Anh, chỉ để cắt ngân sách
    public const int CharsPerToken = 4;

    private static readonly Regex EndHeader = new(@"conclu|discussion|summary|final (remarks|thoughts)|implications",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ContextResult Build(string rawText, ContextOptions? options = null)
    {
        var o = options ?? new ContextOptions();
        string cleaned = TextClean.Clean(rawText, keepHeaders: true);

        if (o.Mode == ContextMode.Full)
        {
            string full = Cut(cleaned, o.FullTokens * CharsPerToken);
            return new(full, [], Tokens(full));
        }

        var parts = Parts(rawText, cleaned, o);
        if (o.Mode == ContextMode.Sections)
        {
            string text = string.Join("\n\n", parts.Select(p => $"=== {p.Label} ===\n{p.Body}"));
            return new(text, [], Tokens(text));
        }

        // Numbered: mỗi câu một số [n], dòng header giữ lại không đánh số để LLM vẫn thấy cấu trúc
        var sentences = new List<string>();
        var sb = new StringBuilder();
        foreach (var part in parts)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append($"=== {part.Label} ===\n");
            foreach (var section in TextClean.Sections(part.Body))
            {
                if (section.Header is not null) sb.Append($"## {section.Header}\n");
                foreach (string s in TextClean.Sentences(section.Body))
                {
                    sentences.Add(s);
                    sb.Append($"[{sentences.Count}] {s}\n");
                }
            }
        }
        string numbered = sb.ToString().TrimEnd();
        return new(numbered, sentences, Tokens(numbered));
    }

    private sealed record Part(string Label, string Body);

    private static List<Part> Parts(string rawText, string cleaned, ContextOptions o)
    {
        int introChars = o.IntroTokens * CharsPerToken, endChars = o.EndTokens * CharsPerToken, midChars = o.MiddleTokens * CharsPerToken;
        string? title = TextClean.TrailingTitle(rawText);
        var parts = new List<Part>();
        if (title is not null) parts.Add(new("LAST LINE OF THE EXTRACTED TEXT (OFTEN THE PAPER TITLE)", title));

        // Bài ngắn: đưa cả bài, cắt ra chỉ làm mất chữ
        if (cleaned.Length <= introChars + endChars + midChars)
        {
            parts.Add(new("FULL PAPER", cleaned));
            return parts;
        }

        string intro = Cut(cleaned, introChars);
        int endStart = EndStart(cleaned, endChars, minStart: intro.Length);
        string end = Cut(cleaned[endStart..], endChars);
        string middleText = cleaned[intro.Length..endStart];

        var cues = new List<string>();
        int used = 0;
        foreach (string s in TextClean.Sentences(TextClean.Clean(middleText, keepHeaders: false)))
        {
            if (!CueExtract.Cue.IsMatch(s)) continue;
            if (used + s.Length > midChars) break;
            cues.Add(s);
            used += s.Length + 1;
        }

        parts.Add(new("BEGINNING OF THE PAPER", intro));
        if (cues.Count > 0) parts.Add(new("KEY SENTENCES FROM THE MIDDLE OF THE PAPER", string.Join("\n", cues)));
        parts.Add(new("END OF THE PAPER", end));
        return parts;
    }

    // Header kết luận/thảo luận cuối cùng nằm sau phần mở đầu; không có thì lấy đoạn cuối bài
    private static int EndStart(string cleaned, int endChars, int minStart)
    {
        int best = -1;
        foreach (Match m in Regex.Matches(cleaned, @"^#+ *(.*)$", RegexOptions.Multiline))
            if (m.Index >= minStart && EndHeader.IsMatch(m.Groups[1].Value)) best = m.Index;
        if (best >= 0) return best;

        int start = Math.Max(minStart, cleaned.Length - endChars);
        int para = cleaned.IndexOf("\n\n", start, StringComparison.Ordinal);
        return para >= 0 && para < cleaned.Length - 200 ? para + 2 : start;
    }

    // Cắt ở ranh giới đoạn, không được thì ở ranh giới câu, để không đưa nửa câu cho LLM
    public static string Cut(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        string head = text[..maxChars];
        int cut = head.LastIndexOf("\n\n", StringComparison.Ordinal);
        if (cut < maxChars / 2) cut = Math.Max(head.LastIndexOf(". ", StringComparison.Ordinal) + 1, 0);
        if (cut < maxChars / 2) cut = maxChars;
        return head[..cut].TrimEnd();
    }

    private static int Tokens(string s) => (s.Length + CharsPerToken - 1) / CharsPerToken;
}
