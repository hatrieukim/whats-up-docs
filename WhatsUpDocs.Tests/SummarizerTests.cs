using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

public sealed class SummarizerTests
{
    [Theory]
    [InlineData("{\"sentences\": [12, 3, 45]}", new[] { 12, 3, 45 })]
    [InlineData("```json\n{\"sentences\":[7,7,2]}\n```", new[] { 7, 2 })]
    [InlineData("Here you go: [4, 5] and finally [9, 1]", new[] { 9, 1 })]
    [InlineData("no numbers at all", new int[0])]
    public void Parses_picked_sentence_numbers(string raw, int[] expected) =>
        Assert.Equal(expected, Summarizer.ParsePicked(raw));

    private static readonly string[] Sents =
    [
        "one two three four five six",          // 1: 6 từ
        "a b c d e f g h",                      // 2: 8 từ
        "alpha beta gamma delta epsilon zeta",  // 3: 6 từ
    ];

    [Fact]
    public void Assemble_keeps_document_order_and_skips_invalid_numbers()
    {
        var (summary, used) = Summarizer.Assemble([3, 99, 1, 0], Sents, maxWords: 100);
        Assert.Equal([1, 3], used);
        Assert.Equal("one two three four five six alpha beta gamma delta epsilon zeta", summary);
    }

    // Theo thứ tự quan trọng: lấy 3 (6 từ), bỏ 2 vì 6+8 > 13, lấy 1 (6+6 = 12)
    [Fact]
    public void Assemble_respects_max_words_by_importance()
    {
        var (_, used) = Summarizer.Assemble([3, 2, 1], Sents, maxWords: 13);
        Assert.Equal([1, 3], used);
    }

    [Fact]
    public void Assemble_always_keeps_first_sentence_even_if_too_long()
    {
        var (_, used) = Summarizer.Assemble([2], Sents, maxWords: 3);
        Assert.Equal([2], used);
    }

    [Fact]
    public void Render_fills_all_placeholders()
    {
        var c = new PromptConfig(TargetWords: 190, MaxWords: 240, MinSentences: 4, MaxSentences: 9);
        string r = Summarizer.Render("{{min_sentences}}-{{max_sentences}} {{target_words}}/{{max_words}}: {{document}}", c, "DOC");
        Assert.Equal("4-9 190/240: DOC", r);
    }

    [Fact]
    public void Validation_requires_document_placeholder_and_sane_config()
    {
        Assert.NotNull(Prompts.Validate("no placeholder", new PromptConfig()));
        Assert.NotNull(Prompts.Validate("{{document}}", new PromptConfig(MinSentences: 5, MaxSentences: 2)));
        Assert.Null(Prompts.Validate(Prompts.DefaultSelect, new PromptConfig()));
    }

    [Fact]
    public void Hash_changes_with_content_or_config()
    {
        string h = Prompts.Hash("x {{document}}", new PromptConfig().ToJson());
        Assert.NotEqual(h, Prompts.Hash("y {{document}}", new PromptConfig().ToJson()));
        Assert.NotEqual(h, Prompts.Hash("x {{document}}", new PromptConfig(MaxWords: 300).ToJson()));
        Assert.Equal(h, Prompts.Hash("x {{document}}", new PromptConfig().ToJson()));
    }
}
