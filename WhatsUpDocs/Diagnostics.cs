using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public sealed record DocDiagnosis(
    long PaperId, double? Score, double RefInPaper, double RefInContext, double RefInOutput, double OracleContext, double OracleFull);

public sealed record RunDiagnosis(
    long RunId, int Docs, double? Score, double RefInPaper, double RefInContext, double RefInOutput,
    double OracleContext, double OracleFull, List<DocDiagnosis> PerDoc);

// Trả lời câu "mất điểm ở đâu": context thiếu câu đáp án, hay model không chọn được câu đáp án đã có trong context
public static class Diagnostics
{
    private static readonly Regex NumberedLine = new(@"^\[(\d+)\] (.*)$", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly ConcurrentDictionary<(long, string), RunDiagnosis> Cache = new();

    public static List<string> ContextSentences(string? contextText) =>
        contextText is null ? [] : NumberedLine.Matches(contextText).Select(m => m.Groups[2].Value).ToList();

    // Tỉ lệ cặp từ (không lặp) của abstract có mặt trong văn bản
    public static double Coverage(string reference, string text)
    {
        var refSet = Rouge2.Bigrams(Rouge2.Tokenize(reference)).Keys.ToHashSet();
        if (refSet.Count == 0) return 0;
        var textSet = Rouge2.Bigrams(Rouge2.Tokenize(text)).Keys;
        return (double)refSet.Count(textSet.Contains) / refSet.Count;
    }

    // Chọn câu tham lam để tối đa ROUGE-2 so với abstract thật: trần điểm nếu "chọn câu hoàn hảo"
    public static double Oracle(IReadOnlyList<string> sentences, string reference, int maxSentences = 15) =>
        OraclePick(sentences, reference, maxSentences).F1;

    // Như Oracle nhưng trả cả chỉ số câu (0-based) theo thứ tự chọn, tức câu đóng góp nhiều nhất đứng đầu
    public static (List<int> Order, double F1) OraclePick(IReadOnlyList<string> sentences, string reference,
        int maxSentences = 15, int maxWords = int.MaxValue)
    {
        var target = Rouge2.Bigrams(Rouge2.Tokenize(reference));
        var grams = sentences.Select(s => Rouge2.Bigrams(Rouge2.Tokenize(s))).ToList();
        var chosen = new List<int>();
        var current = new Dictionary<string, int>();
        double best = 0;
        int words = 0;
        while (chosen.Count < maxSentences)
        {
            int pick = -1;
            double pickF = best;
            for (int i = 0; i < grams.Count; i++)
            {
                if (chosen.Contains(i) || grams[i].Count == 0) continue;
                if (chosen.Count > 0 && words + TextClean.Words(sentences[i]) > maxWords) continue;
                double f = Rouge2.Score(Merge(current, grams[i]), target).F1;
                if (f > pickF) { pickF = f; pick = i; }
            }
            if (pick < 0) break;
            chosen.Add(pick);
            current = Merge(current, grams[pick]);
            words += TextClean.Words(sentences[pick]);
            best = pickF;
        }
        if (chosen.Count == 0) return ([], 0);
        var sorted = chosen.Order().ToList();
        return (chosen, Rouge2.Score(string.Join(" ", sorted.Select(i => sentences[i])), reference).F1);
    }

    private static Dictionary<string, int> Merge(Dictionary<string, int> a, Dictionary<string, int> b)
    {
        var m = new Dictionary<string, int>(a);
        foreach (var (k, v) in b) m[k] = m.GetValueOrDefault(k) + v;
        return m;
    }

    public static DocDiagnosis Doc(DocRow doc, OutputRow output)
    {
        string reference = doc.Summary!;
        string paper = TextClean.Clean(doc.Text, keepHeaders: false);
        var contextSentences = ContextSentences(output.ContextText);
        return new(doc.PaperId, output.Score,
            Coverage(reference, paper),
            Coverage(reference, string.Join(" ", contextSentences)),
            output.Summary is null ? 0 : Coverage(reference, output.Summary),
            Oracle(contextSentences, reference),
            Oracle(TextClean.Sentences(paper), reference));
    }

    // Kết quả theo lượt không đổi khi lượt đã xong nên giữ trong RAM (khoá kèm thời điểm xong)
    public static RunDiagnosis Run(RunRow run, RunStore store, AppDb db)
    {
        var key = (run.Id, run.FinishedAt ?? "");
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var outputs = store.Outputs(run.Id, withContext: true).Where(o => o.Error is null).ToDictionary(o => o.PaperId);
        var docs = db.GetDocs(outputs.Keys).Where(d => d.Summary is not null).ToList();
        var per = docs.AsParallel().Select(d => Doc(d, outputs[d.PaperId])).OrderBy(d => d.PaperId).ToList();
        double Avg(Func<DocDiagnosis, double> f) => per.Count == 0 ? 0 : per.Average(f);
        var result = new RunDiagnosis(run.Id, per.Count, per.Count == 0 ? null : per.Average(d => d.Score ?? 0),
            Avg(d => d.RefInPaper), Avg(d => d.RefInContext), Avg(d => d.RefInOutput), Avg(d => d.OracleContext), Avg(d => d.OracleFull), per);
        if (run.Status != "running") Cache[key] = result;
        return result;
    }
}
