using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public readonly record struct RougeScore(double Precision, double Recall, double F1);

// 1:1 port of the organizers' scorer (Google rouge_score, use_stemmer=False), see src/official_rouge.py.
// The order of operations is kept so double results match Python exactly.
public static class Rouge2
{
    private static readonly Regex NonAlnum = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    public static List<string> Tokenize(string text)
    {
        // Python lower() maps İ to "i" + a combining dot (full case mapping), .NET gives just "i".
        // The combining mark then becomes a space and splits the token in two; we must do the same
        string lower = text.Replace("İ", "i̇").ToLowerInvariant();
        string spaced = NonAlnum.Replace(lower, " ");
        var tokens = new List<string>();
        foreach (string t in Spaces.Split(spaced))
            if (t.Length > 0) tokens.Add(t);
        return tokens;
    }

    public static Dictionary<string, int> Bigrams(IReadOnlyList<string> tokens)
    {
        var counts = new Dictionary<string, int>();
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            string key = tokens[i] + " " + tokens[i + 1];
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        return counts;
    }

    public static RougeScore Score(string prediction, string reference) =>
        Score(Bigrams(Tokenize(prediction)), Bigrams(Tokenize(reference)));

    public static RougeScore Score(Dictionary<string, int> pred, Dictionary<string, int> target)
    {
        int intersection = 0;
        foreach (var (gram, count) in target)
            if (pred.TryGetValue(gram, out int c)) intersection += Math.Min(count, c);
        int targetCount = target.Values.Sum();
        int predCount = pred.Values.Sum();

        double precision = (double)intersection / Math.Max(predCount, 1);
        double recall = (double)intersection / Math.Max(targetCount, 1);
        double f = precision + recall > 0 ? 2 * precision * recall / (precision + recall) : 0.0;
        return new RougeScore(precision, recall, f);
    }
}
