using System.Security.Cryptography;
using System.Text;
using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

[Collection("corpus")]
public sealed class RougeTests(Corpus corpus)
{
    // Điểm do code chính thức của BTC tính (src/make_fixtures.py) phải khớp tuyệt đối, không dung sai
    [Fact]
    public void Matches_official_scorer_exactly_on_all_fixture_pairs()
    {
        var mismatches = new List<string>();
        int n = 0;
        foreach (var row in Corpus.ReadJsonl("rouge2_pairs.jsonl"))
        {
            n++;
            var s = Rouge2.Score(row.GetProperty("pred").GetString()!, row.GetProperty("ref").GetString()!);
            if (s.Precision != row.GetProperty("p").GetDouble() || s.Recall != row.GetProperty("r").GetDouble() || s.F1 != row.GetProperty("f").GetDouble())
                mismatches.Add($"#{n}: C# {s.F1:R} vs Python {row.GetProperty("f").GetDouble():R} | pred={Short(row.GetProperty("pred").GetString()!)}");
        }
        Assert.True(n > 4000, $"Fixture chỉ có {n} cặp");
        Assert.True(mismatches.Count == 0, $"{mismatches.Count}/{n} cặp lệch:\n" + string.Join("\n", mismatches.Take(10)));
    }

    // Token hoá cả 1.345 bài + 1.000 abstract giống Python từng token
    [Fact]
    public void Tokenizes_whole_corpus_like_official_tokenizer()
    {
        var mismatches = new List<string>();
        int n = 0;
        foreach (var row in Corpus.ReadJsonl("tokens.jsonl"))
        {
            n++;
            long id = row.GetProperty("id").GetInt64();
            var doc = corpus.Db.GetDoc(id)!;
            string field = row.GetProperty("field").GetString()!;
            var tokens = Rouge2.Tokenize(field == "text" ? doc.Text : doc.Summary!);
            string sha = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" ", tokens)))).ToLowerInvariant();
            if (tokens.Count != row.GetProperty("n").GetInt32() || sha != row.GetProperty("sha").GetString())
                mismatches.Add($"{id}/{field}: {tokens.Count} vs {row.GetProperty("n").GetInt32()} token");
        }
        Assert.Equal(1000 + 1000 + 345, n);
        Assert.True(mismatches.Count == 0, $"{mismatches.Count} văn bản lệch:\n" + string.Join("\n", mismatches.Take(10)));
    }

    [Theory]
    [InlineData("the cat sat", "the cat sat", 1.0)]
    [InlineData("", "the cat", 0.0)]
    [InlineData("the cat", "", 0.0)]
    [InlineData("a", "a", 0.0)]
    public void Edge_cases(string pred, string reference, double expected) =>
        Assert.Equal(expected, Rouge2.Score(pred, reference).F1);

    [Fact]
    public void Counts_repeated_bigrams_by_min()
    {
        // pred có "the the" ×2, ref có ×1 → khớp 1; P = 1/2, R = 1/1
        var s = Rouge2.Score("the the the", "the the");
        Assert.Equal(0.5, s.Precision);
        Assert.Equal(1.0, s.Recall);
    }

    [Fact]
    public void Splits_on_non_ascii_and_punctuation()
    {
        Assert.Equal(["covid", "19", "don", "t"], Rouge2.Tokenize("COVID-19 don't"));
        Assert.Equal(["caf", "au", "lait"], Rouge2.Tokenize("Café-Au-Lait"));
        Assert.Equal(["i", "stanbul"], Rouge2.Tokenize("İstanbul"));
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] + "…" : s;
}
