using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;

namespace WhatsUpDocs;

// Cấu hình đi kèm một bản prompt. Strategy hiện chỉ có "select" (P2: LLM chọn số câu, ghép nguyên văn)
public sealed record PromptConfig(
    string Strategy = "select",
    int TargetWords = 200, int MaxWords = 250, int MinSentences = 5, int MaxSentences = 10,
    double Temperature = 0,
    int IntroTokens = 2500, int EndTokens = 1500, int MiddleTokens = 500,
    // Bài mẫu (few-shot): null = không dùng. Bỏ khỏi JSON khi để mặc định để mã hash của các bản cũ không đổi
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] string? Examples = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] int ExampleCount = 0)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static PromptConfig Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : (JsonSerializer.Deserialize<PromptConfig>(json, Json) ?? new()).Normalized();
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public string? Validate() =>
        Strategy != "select" ? "Only the \"select\" strategy exists"
        : MinSentences < 1 || MaxSentences < MinSentences ? "Invalid sentence counts"
        : MaxWords < 20 || TargetWords < 20 ? "Word counts are too small"
        : Temperature is < 0 or > 2 ? "Temperature must be within 0–2"
        : IntroTokens < 0 || EndTokens < 0 || MiddleTokens < 0 ? "Token budgets cannot be negative"
        : Examples is not null && !FewShot.Modes.Contains(Examples) ? "Example mode must be full or short"
        : Examples is not null && ExampleCount is < 1 or > 40 ? "Example count must be within 1–40"
        : null;

    // Ô chọn trên UI gửi "" hoặc "none" khi không dùng bài mẫu
    public PromptConfig Normalized() => Examples is null or "" or "none" ? this with { Examples = null, ExampleCount = 0 } : this;
}

public sealed record PromptRow(long Id, string Content, string? Note, string Config, string CreatedAt)
{
    public string Hash => Prompts.Hash(Content, Config);
}

public sealed class Prompts(AppDb db)
{
    public const string Placeholder = "{{document}}";

    public static string Hash(string content, string config) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content + "\n\u0000\n" + PromptConfig.Parse(config).ToJson())))[..12].ToLowerInvariant();

    public static string? Validate(string content, PromptConfig config) =>
        string.IsNullOrWhiteSpace(content) ? "Prompt is empty"
        : !content.Contains(Placeholder) ? $"Prompt must contain {Placeholder}"
        : config.Examples is not null && !content.Contains(FewShot.Placeholder) ? $"With examples enabled the prompt must contain {FewShot.Placeholder}"
        : config.Examples is null && content.Contains(FewShot.Placeholder) ? $"The prompt contains {FewShot.Placeholder} but no example mode is selected"
        : config.Validate();

    public List<PromptRow> List()
    {
        using var conn = db.Open();
        return conn.Query<PromptRow>("SELECT id AS Id, content AS Content, note AS Note, config AS Config, created_at AS CreatedAt FROM prompts ORDER BY id DESC").ToList();
    }

    public PromptRow? Get(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<PromptRow>("SELECT id AS Id, content AS Content, note AS Note, config AS Config, created_at AS CreatedAt FROM prompts WHERE id = @id", new { id });
    }

    public long Create(string content, string? note, PromptConfig config)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>("INSERT INTO prompts (content, note, config, created_at) VALUES (@content, @note, @config, @now) RETURNING id",
            new { content = Normalize(content), note = Blank(note), config = config.ToJson(), now = DateTimeOffset.UtcNow.ToString("O") });
    }

    public bool Update(long id, string content, string? note, PromptConfig config)
    {
        using var conn = db.Open();
        return conn.Execute("UPDATE prompts SET content = @content, note = @note, config = @config WHERE id = @id",
            new { id, content = Normalize(content), note = Blank(note), config = config.ToJson() }) > 0;
    }

    public long? SeedIfEmpty()
    {
        using var conn = db.Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM prompts") > 0) return null;
        return Create(DefaultSelect, "P2: select verbatim sentences", new PromptConfig());
    }

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Trim();
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Bản P2 đầu tiên. Ý chính: abstract do tác giả ghép từ chính các câu "tuyên bố" trong mở đầu/kết luận,
    // và điểm là trùng cặp từ, nên chỉ cho LLM chọn câu, không cho viết lại
    public const string DefaultSelect = """
        You are reconstructing the abstract of a social-science research paper by selecting sentences from the paper itself.

        The paper's abstract has been removed. Authors usually build their abstract from sentences that also appear, almost word for word, in the introduction and the conclusion: the sentences that state the topic and research question, the data and method, the main findings, and the contribution. Your selection will be scored by word overlap with the real abstract, so pick the sentences the author most likely reused.

        Below, sentences from the beginning, the end and key passages of the paper are numbered in square brackets. Section headings are shown for orientation only and cannot be selected.

        Choose sentences that:
        - state what the paper does: its aim, question or argument ("This paper examines...", "We argue that...", "In this study, we...");
        - describe the data, setting and method ("Using data from...", "We conduct...", "Drawing on interviews with...");
        - report the main findings or conclusions ("We find that...", "The results show...", "Overall, ...");
        - state the contribution or the implications for research or policy.

        Avoid sentences that:
        - review or cite other authors' work, give background statistics, or define terms;
        - describe a single table, figure, model specification or robustness check;
        - outline the structure of the paper ("Section 2 describes...", "The remainder of this paper...");
        - repeat what an already selected sentence says.

        Select {{min_sentences}} to {{max_sentences}} sentences, about {{target_words}} words in total. List the most important sentence first.

        Answer with JSON only, in exactly this form, with no other text:
        {"sentences": [12, 3, 45]}

        PAPER:
        {{document}}
        """;
}
