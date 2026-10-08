using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

[Collection("corpus")]
public sealed class DataTests(Corpus corpus)
{
    [Fact]
    public void Imports_all_docs_once()
    {
        Assert.Equal((1000, 345), corpus.Imported);
        Assert.Equal((1000L, 345L), corpus.Db.CountDocs());
        Assert.Equal((0, 0), corpus.Db.ImportIfEmpty(Corpus.Data("train.csv"), Corpus.Data("test_features.csv")));
        Assert.Equal(0, corpus.Db.EnsureSets());
    }

    [Fact]
    public void Test_ids_match_submission_format()
    {
        var expected = File.ReadLines(Corpus.Data("submission_format.csv")).Skip(1)
            .Select(l => long.Parse(l.Split(',')[0])).Order().ToList();
        Assert.Equal(expected, corpus.Db.GetSet("test"));
    }

    [Fact]
    public void Train_docs_have_summary_and_test_docs_do_not()
    {
        Assert.All(corpus.Db.GetDocs(corpus.Db.GetSet("dev")), d => Assert.False(string.IsNullOrWhiteSpace(d.Summary)));
        Assert.All(corpus.Db.GetDocs(corpus.Db.GetSet("test").Take(20)), d => Assert.Null(d.Summary));
    }

    [Fact]
    public void Sets_have_expected_sizes_and_do_not_overlap()
    {
        var s = corpus.Db.GetSets();
        Assert.Equal(30, s["quick"].Count);
        Assert.Equal(150, s["dev"].Count);
        Assert.Equal(100, s["holdout"].Count);
        Assert.Equal(750, s["pool"].Count);
        Assert.Equal(345, s["test"].Count);
        Assert.Subset(s["dev"].ToHashSet(), s["quick"].ToHashSet());

        var all = s["dev"].Concat(s["holdout"]).Concat(s["pool"]).ToList();
        Assert.Equal(1000, all.Distinct().Count());
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Empty(all.Intersect(s["test"]));
    }

    // Python tái lập được đúng cách chia (fixture do src/make_fixtures.py sinh)
    [Fact]
    public void Splits_match_python_and_are_deterministic()
    {
        var python = Corpus.ReadJson<Dictionary<string, List<long>>>("splits.json");
        var sets = corpus.Db.GetSets();
        foreach (var name in Splits.Names) Assert.Equal(python[name], sets[name]);

        var ids = Enumerable.Range(0, 1000).Select(i => (long)i).ToList();
        var shuffled = ids.OrderByDescending(i => i).ToList();
        Assert.Equal(Splits.Make(ids, []), Splits.Make(shuffled, []));
    }
}
