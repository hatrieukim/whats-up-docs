using System.Text.RegularExpressions;

namespace WhatsUpDocs;

public sealed record SummaryResult(
    string Context, string Prompt, string? Raw, string? Summary, IReadOnlyList<int> Picked, IReadOnlyList<int> Used,
    int Words, long? InputTokens, long? OutputTokens, long LatencyMs, string? Error);

public sealed class Summarizer(ILlm llm)
{
    public static string Render(string template, PromptConfig c, string document, string? examples = null) => template
        .Replace(FewShot.Placeholder, examples ?? "")
        .Replace("{{target_words}}", c.TargetWords.ToString())
        .Replace("{{max_words}}", c.MaxWords.ToString())
        .Replace("{{min_sentences}}", c.MinSentences.ToString())
        .Replace("{{max_sentences}}", c.MaxSentences.ToString())
        .Replace(Prompts.Placeholder, document);

    public static ContextResult Context(string docText, PromptConfig c) =>
        ContextBuilder.Build(docText, new ContextOptions(ContextMode.Numbered, c.IntroTokens, c.EndTokens, c.MiddleTokens));

    private static readonly Regex SentencesField = new(@"""sentences""\s*:\s*\[([^\]]*)\]", RegexOptions.Compiled);
    private static readonly Regex AnyIntList = new(@"\[\s*(\d+(?:\s*,\s*\d+)*)\s*\]", RegexOptions.Compiled);
    private static readonly Regex Int = new(@"\d+", RegexOptions.Compiled);

    // Model đôi khi bọc ```json hoặc thêm chữ: lấy trường "sentences", không có thì mảng số cuối cùng
    public static List<int> ParsePicked(string raw)
    {
        var m = SentencesField.Match(raw);
        string? list = m.Success ? m.Groups[1].Value : AnyIntList.Matches(raw).LastOrDefault()?.Groups[1].Value;
        return list is null ? [] : Int.Matches(list).Select(x => int.Parse(x.Value)).Distinct().ToList();
    }

    // Lấy câu theo thứ tự quan trọng LLM đưa, dừng khi vượt MaxWords (luôn giữ ít nhất 1 câu), rồi xếp lại theo thứ tự trong bài
    public static (string Summary, List<int> Used) Assemble(IReadOnlyList<int> picked, IReadOnlyList<string> sentences, int maxWords)
    {
        var used = new List<int>();
        int words = 0;
        foreach (int n in picked)
        {
            if (n < 1 || n > sentences.Count) continue;
            int w = TextClean.Words(sentences[n - 1]);
            if (used.Count > 0 && words + w > maxWords) continue;
            used.Add(n);
            words += w;
        }
        used.Sort();
        return (string.Join(" ", used.Select(n => sentences[n - 1])), used);
    }

    public async Task<SummaryResult> RunAsync(string docText, string template, PromptConfig config, string model, CancellationToken ct,
        string? examples = null)
    {
        var context = Context(docText, config);
        string prompt = Render(template, config, context.Text, examples);
        LlmReply? reply = null;
        try
        {
            reply = await RetryAsync(() => llm.CompleteAsync(model, prompt, config.Temperature, ct), ct);
            var picked = ParsePicked(reply.Text);
            if (picked.Count == 0) return Fail("Could not read a list of sentence numbers from the output");
            var (summary, used) = Assemble(picked, context.Sentences, config.MaxWords);
            if (used.Count == 0) return Fail($"All sentence numbers are outside 1–{context.Sentences.Count}");
            return new(context.Text, prompt, reply.Text, summary, picked, used, TextClean.Words(summary),
                reply.InputTokens, reply.OutputTokens, reply.LatencyMs, null);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return Fail($"{ex.GetType().Name}: {ex.Message}");
        }

        SummaryResult Fail(string error) => new(context.Text, prompt, reply?.Text, null, [], [], 0,
            reply?.InputTokens, reply?.OutputTokens, reply?.LatencyMs ?? 0, error);
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action();
            }
            catch (Exception) when (attempt < 3 && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt), ct);
            }
        }
    }
}
