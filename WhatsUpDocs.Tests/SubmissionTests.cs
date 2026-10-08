using System.Globalization;
using CsvHelper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

[Collection("corpus")]
public sealed class SubmissionTests(Corpus corpus) : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), $"whatsupdocs-sub-{Guid.NewGuid():N}");

    private (RunService Service, RunStore Store, Prompts Prompts, FakeLlm Llm, Submissions Subs) Make()
    {
        var llm = new FakeLlm();
        var prompts = new Prompts(corpus.Db);
        var store = new RunStore(corpus.Db);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["OPENAI_MODEL"] = "fake" }).Build();
        var service = new RunService(corpus.Db, prompts, store, new Summarizer(llm), new FewShot(corpus.Db), config, new TestLifetime(), NullLogger<RunService>.Instance);
        return (service, store, prompts, llm, new Submissions(corpus.Db, store, Corpus.Data("submission_format.csv"), _out));
    }

    private static async Task<RunRow> WaitAsync(RunService service, RunStore store, long id)
    {
        for (int i = 0; i < 1200 && service.IsRunning(id); i++) await Task.Delay(50);
        return store.Get(id)!;
    }

    [Fact]
    public async Task Rerun_on_test_produces_a_valid_submission_file()
    {
        var (service, store, prompts, llm, subs) = Make();
        long pid = prompts.SeedIfEmpty() ?? prompts.List().Last().Id;
        var (src, _) = service.Start(new RunRequest("src", pid, "fake-model", "quick", 8));
        await WaitAsync(service, store, src!.Value);

        // Sửa prompt sau khi chạy: lượt test vẫn phải dùng bản đã chụp
        prompts.Update(pid, "CHANGED {{document}}", null, new PromptConfig());
        llm.BrokenMarkers.Add("CHANGED");

        var (testRun, err) = service.Rerun(src.Value, "test", null, 16);
        Assert.Null(err);
        var run = await WaitAsync(service, store, testRun!.Value);
        Assert.Equal("done", run.Status);
        Assert.Equal(345, run.Done);
        Assert.Equal(0, run.Errors);
        Assert.Equal("fake-model", run.Model);
        Assert.Equal($"[{src.Value}]", run.ParentRuns);
        Assert.Null(run.Score);

        var check = subs.Check(run.Id);
        Assert.True(check.Ok, check.Problem);
        var (row, createErr) = subs.Create(run.Id, "thử");
        Assert.Null(createErr);

        using var reader = new StreamReader(row!.FilePath);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        var records = csv.GetRecords<SubmissionLine>().ToList();
        Assert.Equal(subs.ExpectedIds(), records.Select(r => r.paper_id).ToList());
        Assert.All(records, r => Assert.False(string.IsNullOrWhiteSpace(r.summary)));
        Assert.Equal(File.ReadLines(Corpus.Data("submission_format.csv")).First(), File.ReadLines(row.FilePath).First());

        Assert.True(subs.Update(row.Id, new SubmissionUpdate(0.2, "điểm public")));
        Assert.Equal(0.2, subs.Get(row.Id)!.PublicScore);
    }

    [Fact]
    public async Task Check_blocks_a_run_with_errors()
    {
        var (service, store, prompts, llm, subs) = Make();
        long pid = prompts.Create("BROKEN {{document}}", "broken", new PromptConfig());
        llm.BrokenMarkers.Add("BROKEN");
        var (src, _) = service.Start(new RunRequest("src", pid, null, "quick", 8));
        await WaitAsync(service, store, src!.Value);
        var check = subs.Check(src.Value);
        Assert.False(check.Ok);
        Assert.NotNull(subs.Create(src.Value, null).Error);
    }

    private sealed record SubmissionLine(long paper_id, string summary);

    public void Dispose()
    {
        if (Directory.Exists(_out)) Directory.Delete(_out, recursive: true);
    }
}
