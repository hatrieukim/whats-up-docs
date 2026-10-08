using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

[Collection("corpus")]
public sealed class DiagnosticsTests(Corpus corpus)
{
    [Fact]
    public void Coverage_is_share_of_reference_bigrams_present()
    {
        Assert.Equal(1.0, Diagnostics.Coverage("a b c", "x a b c y"));
        Assert.Equal(0.5, Diagnostics.Coverage("a b c", "a b"));
        Assert.Equal(0.0, Diagnostics.Coverage("a b c", "c b a"));
    }

    [Fact]
    public void Oracle_picks_the_sentences_that_rebuild_the_reference()
    {
        string[] sents = ["noise words here only", "we find that trust declines", "data come from a panel survey", "unrelated filler text"];
        double f = Diagnostics.Oracle(sents, "We find that trust declines. Data come from a panel survey.");
        // Hai câu đúng ghép lại thiếu đúng 1 cặp từ nối giữa hai câu ("declines data") so với abstract
        Assert.True(f > 0.9, $"oracle = {f}");
        Assert.Equal(0, Diagnostics.Oracle(["x y z"], "a b c"));
    }

    [Fact]
    public void Context_sentences_are_parsed_from_numbered_context()
    {
        var doc = corpus.Db.GetDocs(corpus.Db.GetSet("quick")).First();
        var ctx = ContextBuilder.Build(doc.Text, new ContextOptions(ContextMode.Numbered));
        Assert.Equal(ctx.Sentences, Diagnostics.ContextSentences(ctx.Text));
    }

    // Trần của cả bài phải ≥ trần của context (context là tập con câu của bài), trên mọi bài quick
    [Fact]
    public void Full_paper_oracle_is_at_least_context_oracle_on_average()
    {
        var docs = corpus.Db.GetDocs(corpus.Db.GetSet("quick"));
        var diags = docs.Select(d =>
        {
            var ctx = ContextBuilder.Build(d.Text, new ContextOptions(ContextMode.Numbered));
            return Diagnostics.Doc(d, new OutputRow { PaperId = d.PaperId, ContextText = ctx.Text, Summary = ctx.Sentences[0], Score = 0 });
        }).ToList();
        Assert.True(diags.Average(x => x.OracleFull) >= diags.Average(x => x.OracleContext) - 0.005);
        Assert.All(diags, x => Assert.InRange(x.RefInContext, 0, x.RefInPaper + 1e-9));
    }
}
