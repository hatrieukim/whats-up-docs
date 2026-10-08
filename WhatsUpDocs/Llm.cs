using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace WhatsUpDocs;

public sealed record LlmReply(string Text, long? InputTokens, long? OutputTokens, long LatencyMs);

// Tách ra interface để test chạy lượt bằng LLM giả, không gọi proxy
public interface ILlm
{
    Task<LlmReply> CompleteAsync(string model, string prompt, double temperature, CancellationToken ct);
}

public sealed class MafLlm(OpenAIClient client) : ILlm
{
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();

    // Mỗi bài một lời gọi độc lập (luật thi), không giữ session
    public async Task<LlmReply> CompleteAsync(string model, string prompt, double temperature, CancellationToken ct)
    {
        var agent = _agents.GetOrAdd(model, m => client.GetChatClient(m).AsAIAgent(name: "summarizer"));
        var options = new ChatClientAgentRunOptions(new ChatOptions { Temperature = (float)temperature });
        var sw = Stopwatch.StartNew();
        var response = await agent.RunAsync(prompt, options: options, cancellationToken: ct);
        return new(response.Text, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, sw.ElapsedMilliseconds);
    }
}
