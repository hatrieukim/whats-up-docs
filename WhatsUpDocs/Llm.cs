using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;

namespace WhatsUpDocs;

public sealed record LlmReply(string Text, long? InputTokens, long? OutputTokens, long LatencyMs);

// Behind an interface so tests can run with a fake LLM instead of calling the proxy
public interface ILlm
{
    Task<LlmReply> CompleteAsync(string model, string prompt, double temperature, CancellationToken ct);
}

public sealed class MafLlm(OpenAIClient client) : ILlm
{
    private readonly ConcurrentDictionary<string, AIAgent> _agents = new();

    // One independent call per paper (competition rule), no session kept
    public async Task<LlmReply> CompleteAsync(string model, string prompt, double temperature, CancellationToken ct)
    {
        var agent = _agents.GetOrAdd(model, m => client.GetChatClient(m).AsAIAgent(name: "summarizer"));
        var options = new ChatClientAgentRunOptions(new ChatOptions { Temperature = (float)temperature });
        var sw = Stopwatch.StartNew();
        var response = await agent.RunAsync(prompt, options: options, cancellationToken: ct);
        return new(response.Text, response.Usage?.InputTokenCount, response.Usage?.OutputTokenCount, sw.ElapsedMilliseconds);
    }
}
