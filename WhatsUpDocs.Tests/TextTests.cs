using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

[Collection("corpus")]
public sealed class TextTests(Corpus corpus)
{
    [Fact]
    public void Clean_removes_noise_the_abstracts_never_have()
    {
        const string raw = "## Intro\n\nWe study R&amp;D (Smith, 2020; Lee 2019a) in firms [12].\n\n<!-- image -->\n\nFigure 3: Trends\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\nTable 2 Results\n\nEnd.";
        Assert.Equal("## Intro\n\nWe study R&D in firms.\n\nEnd.", TextClean.Clean(raw, keepHeaders: true));
        Assert.Equal("We study R&D in firms.\n\nEnd.", TextClean.Clean(raw, keepHeaders: false));
    }

    [Fact]
    public void Clean_decodes_double_encoded_entities() =>
        Assert.Equal("R&D and a < b", TextClean.Clean("R&amp;amp;D and a &amp;lt; b", keepHeaders: false));

    [Fact]
    public void Clean_keeps_non_citation_parentheses()
    {
        Assert.Equal("Adults (aged over 60) were sampled.", TextClean.Clean("Adults (aged over 60) were sampled.", keepHeaders: false));
    }

    [Fact]
    public void Sentences_split_and_filter_by_length()
    {
        var s = TextClean.Sentences("Too short. This sentence is long enough to be kept here. Another sentence that also has enough words!");
        Assert.Equal(["This sentence is long enough to be kept here.", "Another sentence that also has enough words!"], s);
    }

    [Fact]
    public void Sections_split_on_headers()
    {
        var s = TextClean.Sections("Preface text.\n\n## Introduction\n\nBody one.\n\n### Method\n\nBody two.");
        Assert.Equal([new Section(null, "Preface text."), new Section("Introduction", "Body one."), new Section("Method", "Body two.")], s);
    }

    [Fact]
    public void Trailing_title_detected_only_for_short_unpunctuated_last_line()
    {
        Assert.Equal("Priorities of health research in India", TextClean.TrailingTitle("Body text here.\n\nPriorities of health research in India"));
        Assert.Null(TextClean.TrailingTitle("Body text.\n\nThis ends with a period."));
        Assert.Null(TextClean.TrailingTitle("Body.\n\nWang, B. (2015). Extreme dependence. https://doi.org/10.1016/x"));
    }

    [Fact]
    public void Cut_prefers_paragraph_then_sentence_boundary()
    {
        string text = new string('a', 60) + "\n\n" + new string('b', 60);
        Assert.Equal(new string('a', 60), ContextBuilder.Cut(text, 100));
        Assert.Equal("One two. Three four.", ContextBuilder.Cut("One two. Three four. Five six seven", 25));
    }

    // Mọi bài đều ra context không rỗng, nằm trong ngân sách, và không còn rác
    [Fact]
    public void Context_for_every_doc_is_within_budget()
    {
        var o = new ContextOptions();
        int budget = (o.IntroTokens + o.EndTokens + o.MiddleTokens) * ContextBuilder.CharsPerToken + 1000;
        foreach (var doc in corpus.Db.GetDocs(corpus.Db.GetSet("dev").Concat(corpus.Db.GetSet("test"))))
        {
            var c = ContextBuilder.Build(doc.Text);
            Assert.False(string.IsNullOrWhiteSpace(c.Text), $"bài {doc.PaperId} context rỗng");
            Assert.True(c.Text.Length <= budget, $"bài {doc.PaperId}: {c.Text.Length} ký tự > {budget}");
            Assert.DoesNotContain("<!-- image -->", c.Text);
            Assert.DoesNotContain("&amp;", c.Text);
        }
    }

    [Fact]
    public void Numbered_context_lists_each_sentence_once_in_order()
    {
        var doc = corpus.Db.GetDocs(corpus.Db.GetSet("quick")).First();
        var c = ContextBuilder.Build(doc.Text, new ContextOptions(ContextMode.Numbered));
        Assert.True(c.Sentences.Count > 20);
        for (int i = 0; i < c.Sentences.Count; i++)
            Assert.Contains($"[{i + 1}] {c.Sentences[i]}\n", c.Text + "\n");
    }

    [Fact]
    public void Full_context_respects_token_budget()
    {
        var doc = corpus.Db.GetDocs(corpus.Db.GetSet("dev")).MaxBy(d => d.Chars)!;
        var c = ContextBuilder.Build(doc.Text, new ContextOptions(ContextMode.Full, FullTokens: 5000));
        Assert.True(c.Text.Length <= 5000 * ContextBuilder.CharsPerToken);
    }

    // Bản C# có thêm bước bỏ bảng nên không bắt buộc trùng từng bài; điểm trung bình phải sát bản Python
    [Fact]
    public void Cue_extract_on_dev_is_close_to_python()
    {
        var python = Corpus.ReadJson<CueFixture>("cue_dev.json");
        var docs = corpus.Db.GetDocs(corpus.Db.GetSet("dev"));
        double mean = docs.Average(d => Rouge2.Score(CueExtract.Run(d.Text), d.Summary!).F1);
        Assert.InRange(mean, python.mean - 0.005, python.mean + 0.005);
    }

    private sealed record CueFixture(double mean, Dictionary<string, double> perDoc);
}
