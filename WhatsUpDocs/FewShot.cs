using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public sealed record ExampleSentence(int Number, string Part, string Text);

// Một bài mẫu: context đánh số y như lúc chạy thật, và "đáp án" = các câu oracle chọn so với abstract thật
public sealed record ExampleDoc(long PaperId, double OracleF1, int ContextTokens, string Context,
    List<int> Answer, List<ExampleSentence> Positives, List<ExampleSentence> Negatives);

// Bài mẫu cho prompt (few-shot). Chỉ lấy từ tập pool để không lộ đáp án của dev/holdout/test.
// Cùng cấu hình thì luôn ra cùng bộ bài, nên điểm các lượt so được với nhau
public sealed class FewShot(AppDb db)
{
    public const string Placeholder = "{{examples}}";
    public static readonly string[] Modes = ["full", "short"];

    // Bài mẫu đầy đủ chiếm nhiều token nên chỉ lấy bài có context ngắn
    private const int FullMaxTokens = 4000;
    // Bài điển hình: đáp án rõ nhưng không phải kiểu bài chép nguyên khối abstract vào thân bài (oracle > 0.7)
    private const double MinOracle = 0.40, MaxOracle = 0.70, MaxOverlap = 0.08;

    private static readonly Regex PartLine = new(@"^=== (.*) ===$", RegexOptions.Compiled);
    private static readonly Regex NumberedLine = new(@"^\[(\d+)\] ", RegexOptions.Compiled);
    private static readonly Regex LongWord = new(@"[a-z]{5,}", RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, List<ExampleDoc>> _cache = new();

    public List<ExampleDoc> Pick(PromptConfig c)
    {
        if (c.Examples is null || c.ExampleCount <= 0) return [];
        string key = $"{c.Examples}|{c.ExampleCount}|{c.IntroTokens}|{c.EndTokens}|{c.MiddleTokens}|{c.MaxSentences}|{c.MaxWords}";
        return _cache.GetOrAdd(key, _ => Select(c));
    }

    public string? Build(PromptConfig c) => c.Examples is null ? null : Render(c.Examples, Pick(c));

    private List<ExampleDoc> Select(PromptConfig c)
    {
        var docs = db.GetDocs(db.GetSet("pool")).Where(d => d.Summary is not null).ToList();
        var candidates = docs.AsParallel().Select(d => Make(d, c))
            .Where(e => e.OracleF1 is >= MinOracle and <= MaxOracle && e.Answer.Count >= 3)
            .Where(e => c.Examples != "full" || e.ContextTokens <= FullMaxTokens)
            .OrderBy(e => Shuffle(e.PaperId), StringComparer.Ordinal).ToList();

        // Đa dạng chủ đề: bỏ bài có abstract trùng nhiều từ với bài đã chọn
        var chosen = new List<(ExampleDoc Doc, HashSet<string> Words)>();
        var refs = docs.ToDictionary(d => d.PaperId, d => d.Summary!);
        foreach (var e in candidates)
        {
            var words = LongWord.Matches(refs[e.PaperId].ToLowerInvariant()).Select(m => m.Value).ToHashSet();
            if (chosen.Any(x => Jaccard(x.Words, words) > MaxOverlap)) continue;
            chosen.Add((e, words));
            if (chosen.Count == c.ExampleCount) break;
        }
        return chosen.Select(x => x.Doc).ToList();
    }

    public static ExampleDoc Make(DocRow doc, PromptConfig c)
    {
        var context = Summarizer.Context(doc.Text, c);
        var (order, f1) = Diagnostics.OraclePick(context.Sentences, doc.Summary!, c.MaxSentences, c.MaxWords);
        var parts = Parts(context.Text);
        ExampleSentence At(int i) => new(i + 1, parts.GetValueOrDefault(i + 1, "paper"), context.Sentences[i]);

        // Câu "trông giống" câu tuyên bố (có cue) nhưng gần như không có cặp từ nào trong abstract: dạy model cái nên bỏ
        var refGrams = Rouge2.Bigrams(Rouge2.Tokenize(doc.Summary!)).Keys.ToHashSet();
        var negatives = Enumerable.Range(0, context.Sentences.Count)
            .Where(i => !order.Contains(i) && CueExtract.Cue.IsMatch(context.Sentences[i]))
            .Where(i =>
            {
                var g = Rouge2.Bigrams(Rouge2.Tokenize(context.Sentences[i])).Keys.ToList();
                return g.Count > 0 && (double)g.Count(refGrams.Contains) / g.Count < 0.1;
            })
            .Take(2).Select(At).ToList();

        return new(doc.PaperId, f1, context.ApproxTokens, context.Text, order.Select(i => i + 1).ToList(),
            order.Order().Select(At).ToList(), negatives);
    }

    // Số câu → phần của context chứa nó (đầu bài / giữa / cuối)
    private static Dictionary<int, string> Parts(string contextText)
    {
        var map = new Dictionary<int, string>();
        string part = "paper";
        foreach (string line in contextText.Split('\n'))
        {
            if (PartLine.Match(line) is { Success: true } p) part = ShortPart(p.Groups[1].Value);
            else if (NumberedLine.Match(line) is { Success: true } n) map[int.Parse(n.Groups[1].Value)] = part;
        }
        return map;
    }

    private static string ShortPart(string label) => label switch
    {
        "BEGINNING OF THE PAPER" => "beginning",
        "END OF THE PAPER" => "end",
        "FULL PAPER" => "paper",
        _ when label.StartsWith("KEY SENTENCES") => "middle",
        _ => "title",
    };

    public static string Render(string mode, IReadOnlyList<ExampleDoc> examples) =>
        string.Join("\n\n", examples.Select((e, k) => mode == "full" ? Full(e, k + 1) : Short(e, k + 1)));

    private static string Full(ExampleDoc e, int k) =>
        $"=== EXAMPLE {k} ===\nPAPER:\n{e.Context}\n\nANSWER:\n{JsonSerializer.Serialize(new { sentences = e.Answer })}";

    private static string Short(ExampleDoc e, int k)
    {
        var sb = new StringBuilder($"EXAMPLE {k}\nSentences the author reused in the abstract (in paper order, with where they appear):\n");
        foreach (var s in e.Positives) sb.Append($"- ({s.Part}) {s.Text}\n");
        if (e.Negatives.Count > 0)
        {
            sb.Append("Sentences that look relevant but were NOT reused:\n");
            foreach (var s in e.Negatives) sb.Append($"- ({s.Part}) {s.Text}\n");
        }
        return sb.ToString().TrimEnd();
    }

    // Thứ tự xáo cố định theo id (không dùng Random) để bộ mẫu không đổi giữa các lần chạy
    private static string Shuffle(long paperId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"whatsupdocs-fewshot-v1:{paperId}")));

    private static double Jaccard(HashSet<string> a, HashSet<string> b) =>
        a.Count + b.Count == 0 ? 0 : (double)a.Count(b.Contains) / (a.Count + b.Count - a.Count(b.Contains));
}
