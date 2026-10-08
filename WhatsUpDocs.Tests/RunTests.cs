using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

// LLM giả: luôn chọn câu 1–8; bài nằm trong `broken` thì trả chữ không có số → lỗi đọc output (không bị retry)
public sealed class FakeLlm : ILlm
{
    public HashSet<string> BrokenMarkers { get; } = [];
    public int Calls;
    public int DelayMs;

    public async Task<LlmReply> CompleteAsync(string model, string prompt, double temperature, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        if (DelayMs > 0) await Task.Delay(DelayMs, ct);
        bool broken = BrokenMarkers.Any(prompt.Contains);
        return new(broken ? "sorry" : "{\"sentences\": [1, 2, 3, 4, 5, 6, 7, 8]}", 100, 10, 1);
    }
}

public sealed class TestLifetime : IHostApplicationLifetime
{
    public CancellationToken ApplicationStarted => CancellationToken.None;
    public CancellationToken ApplicationStopping => CancellationToken.None;
    public CancellationToken ApplicationStopped => CancellationToken.None;
    public void StopApplication() { }
}

[Collection("corpus")]
public sealed class RunTests(Corpus corpus)
{
    private (RunService Service, RunStore Store, Prompts Prompts, FakeLlm Llm) Make()
    {
        var llm = new FakeLlm();
        var prompts = new Prompts(corpus.Db);
        var store = new RunStore(corpus.Db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OPENAI_MODEL"] = "fake" }).Build();
        var service = new RunService(corpus.Db, prompts, store, new Summarizer(llm), new FewShot(corpus.Db), config, new TestLifetime(), NullLogger<RunService>.Instance);
        return (service, store, prompts, llm);
    }

    private static async Task<RunRow> WaitAsync(RunService service, RunStore store, long id)
    {
        for (int i = 0; i < 600 && service.IsRunning(id); i++) await Task.Delay(50);
        Assert.False(service.IsRunning(id), "Lượt chạy quá lâu");
        return store.Get(id)!;
    }

    [Fact]
    public async Task Run_scores_every_doc_with_the_same_metric()
    {
        var (service, store, prompts, llm) = Make();
        long promptId = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        var (id, err) = service.Start(new RunRequest("test", promptId, null, "quick", 8));
        Assert.Null(err);

        var run = await WaitAsync(service, store, id!.Value);
        Assert.Equal("done", run.Status);
        Assert.Equal(30, run.Done);
        Assert.Equal(0, run.Errors);
        Assert.Equal(30, llm.Calls);

        // Điểm lượt = trung bình F1 tính lại độc lập từ câu 1–8 của context
        var cfg = PromptConfig.Parse(prompts.Get(promptId)!.Config);
        double expected = corpus.Db.GetDocs(corpus.Db.GetSet("quick")).Average(d =>
        {
            var (summary, _) = Summarizer.Assemble([1, 2, 3, 4, 5, 6, 7, 8], Summarizer.Context(d.Text, cfg).Sentences, cfg.MaxWords);
            return Rouge2.Score(summary, d.Summary!).F1;
        });
        Assert.Equal(expected, run.Score!.Value, 12);
        Assert.All(store.Outputs(run.Id), o => Assert.True(o.Words <= cfg.MaxWords || o.Summary!.Split(' ').Length > 0));
    }

    [Fact]
    public async Task Errors_are_counted_and_retry_reruns_only_failed_docs()
    {
        var (service, store, prompts, llm) = Make();
        long promptId = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        var quick = corpus.Db.GetDocs(corpus.Db.GetSet("quick"));
        var cfg = PromptConfig.Parse(prompts.Get(promptId)!.Config);
        // Đánh dấu 3 bài lỗi bằng câu đầu tiên trong context của chúng
        var brokenDocs = quick.Take(3).ToList();
        foreach (var d in brokenDocs) llm.BrokenMarkers.Add(Summarizer.Context(d.Text, cfg).Sentences[0]);

        var (id, _) = service.Start(new RunRequest("errors", promptId, null, "quick", 4));
        var run = await WaitAsync(service, store, id!.Value);
        Assert.Equal(30, run.Done);
        Assert.Equal(3, run.Errors);
        Assert.Equal(27 * 1.0, store.Outputs(run.Id).Count(o => o.Error is null));

        llm.BrokenMarkers.Clear();
        int before = llm.Calls;
        Assert.Null(service.Retry(run.Id));
        run = await WaitAsync(service, store, run.Id);
        Assert.Equal(3, llm.Calls - before);
        Assert.Equal(0, run.Errors);
        Assert.Equal("done", run.Status);
        Assert.Equal("Nothing to retry", service.Retry(run.Id));
    }

    [Fact]
    public async Task Cancel_stops_the_run()
    {
        var (service, store, prompts, llm) = Make();
        llm.DelayMs = 300;
        long promptId = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        var (id, _) = service.Start(new RunRequest("cancel", promptId, null, "dev", 2));
        await Task.Delay(400);
        Assert.True(service.Cancel(id!.Value));
        var run = await WaitAsync(service, store, id.Value);
        Assert.Equal("cancelled", run.Status);
        Assert.True(run.Done < 150);
    }

    [Fact]
    public void Editing_a_prompt_marks_its_runs_stale()
    {
        var (service, store, prompts, _) = Make();
        long pid = prompts.Create("v1 {{document}}", "stale-test", new PromptConfig());
        var (id, _) = service.Start(new RunRequest("stale", pid, null, "quick", 8));
        Assert.Null(service.List().Single(r => r.Id == id).Stale);
        prompts.Update(pid, "v2 {{document}}", "stale-test", new PromptConfig());
        Assert.Equal("prompt edited since", service.List().Single(r => r.Id == id).Stale);
    }

    [Fact]
    public void Start_rejects_unknown_prompt_or_set()
    {
        var (service, _, prompts, _) = Make();
        Assert.NotNull(service.Start(new RunRequest(null, 99999, null, "quick", 1)).Error);
        long pid = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        Assert.NotNull(service.Start(new RunRequest(null, pid, null, "nope", 1)).Error);
    }

    [Fact]
    public void Compare_counts_better_and_worse_docs()
    {
        var store = new RunStore(corpus.Db);
        var prompts = new Prompts(corpus.Db);
        long pid = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        var p = prompts.Get(pid)!;
        var snap = new RunSnapshot(p.Content, new PromptConfig(), 1);
        long a = store.Create("A", p, "fake", "quick", snap), b = store.Create("B", p, "fake", "quick", snap);
        var ids = corpus.Db.GetSet("quick").Take(3).ToList();
        double[] sa = [0.1, 0.2, 0.3], sb = [0.2, 0.2, 0.25];
        for (int i = 0; i < 3; i++)
        {
            store.SaveOutput(new OutputRow { RunId = a, PaperId = ids[i], Score = sa[i], CreatedAt = RunStore.Now() });
            store.SaveOutput(new OutputRow { RunId = b, PaperId = ids[i], Score = sb[i], CreatedAt = RunStore.Now() });
        }
        var c = Compare.Run(store, a, b);
        Assert.Equal((3, 1, 1, 1), (c.Common, c.Better, c.Worse, c.Same));
        Assert.Equal(0.2, c.MeanA!.Value, 12);
    }
}
