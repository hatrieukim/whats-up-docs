using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

public sealed class FewShotConfigTests
{
    // Thêm trường bài mẫu không được làm đổi hash của các bản prompt cũ (nếu không mọi lượt cũ thành "prompt đã sửa")
    [Fact]
    public void Default_config_json_has_no_example_fields()
    {
        string json = new PromptConfig().ToJson();
        Assert.DoesNotContain("example", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(json, PromptConfig.Parse(json).ToJson());
    }

    [Fact]
    public void None_mode_normalizes_to_null()
    {
        var c = PromptConfig.Parse("""{"examples":"none","exampleCount":3}""");
        Assert.Null(c.Examples);
        Assert.Equal(0, c.ExampleCount);
    }

    [Fact]
    public void Placeholder_and_mode_must_match()
    {
        var with = new PromptConfig(Examples: "short", ExampleCount: 5);
        Assert.NotNull(Prompts.Validate("{{document}}", with));
        Assert.NotNull(Prompts.Validate("{{examples}} {{document}}", new PromptConfig()));
        Assert.Null(Prompts.Validate("{{examples}} {{document}}", with));
        Assert.NotNull(new PromptConfig(Examples: "bogus", ExampleCount: 2).Validate());
        Assert.NotNull(new PromptConfig(Examples: "full", ExampleCount: 0).Validate());
    }

    [Fact]
    public void Render_fills_examples() =>
        Assert.Equal("EX | DOC", Summarizer.Render("{{examples}} | {{document}}", new PromptConfig(), "DOC", "EX"));
}

[Collection("corpus")]
public sealed class FewShotTests(Corpus corpus)
{
    [Theory]
    [InlineData("full", 3)]
    [InlineData("short", 20)]
    public void Picks_fixed_examples_from_pool_only(string mode, int count)
    {
        var cfg = new PromptConfig(Examples: mode, ExampleCount: count);
        var a = new FewShot(corpus.Db).Pick(cfg);
        var b = new FewShot(corpus.Db).Pick(cfg);
        Assert.Equal(count, a.Count);
        Assert.Equal(a.Select(e => e.PaperId), b.Select(e => e.PaperId));
        var pool = corpus.Db.GetSet("pool").ToHashSet();
        Assert.All(a, e => Assert.Contains(e.PaperId, pool));
        if (mode == "full") Assert.All(a, e => Assert.True(e.ContextTokens <= 4000));
    }

    [Fact]
    public void Example_answer_points_at_numbered_context_sentences()
    {
        var cfg = new PromptConfig(Examples: "short", ExampleCount: 5);
        foreach (var e in new FewShot(corpus.Db).Pick(cfg))
        {
            Assert.InRange(e.OracleF1, 0.40, 0.70);
            Assert.True(e.Answer.Count >= 3 && e.Answer.Count <= cfg.MaxSentences);
            foreach (var s in e.Positives) Assert.Contains($"[{s.Number}] {s.Text}", e.Context);
            Assert.Equal(e.Answer.Order(), e.Positives.Select(s => s.Number));
            Assert.All(e.Negatives, s => Assert.DoesNotContain(s.Number, e.Answer));
        }
        string text = new FewShot(corpus.Db).Build(cfg)!;
        Assert.Contains("EXAMPLE 5", text);
        Assert.DoesNotContain("EXAMPLE 6", text);
    }
}
