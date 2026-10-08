using System.Collections.Concurrent;
using System.Text.Json;
using Dapper;

namespace WhatsUpDocs;

// Class có setter vì cột tổng hợp (AVG, SUM) trả kiểu không cố định, Dapper tự đổi kiểu qua setter
public sealed class RunRow
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "";
    public long? PromptId { get; set; }
    public string? PromptHash { get; set; }
    public string? Model { get; set; }
    public string Config { get; set; } = "{}";
    public string SetName { get; set; } = "";
    public string? ParentRuns { get; set; }
    public string Status { get; set; } = "";
    public string? Error { get; set; }
    public double? Score { get; set; }
    public double? Precision { get; set; }
    public double? Recall { get; set; }
    public double? AvgWords { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public string StartedAt { get; set; } = "";
    public string? FinishedAt { get; set; }
    public long Total { get; set; }
    public long Done { get; set; }
    public long Errors { get; set; }
    // Không có trong DB: lý do điểm đã cũ (prompt bị sửa sau khi chạy), null nếu còn đúng
    public string? Stale { get; set; }
}

public sealed class OutputRow
{
    public long RunId { get; set; }
    public long PaperId { get; set; }
    public string? ContextText { get; set; }
    public string? RawOutput { get; set; }
    public string? Summary { get; set; }
    public double? Score { get; set; }
    public double? Precision { get; set; }
    public double? Recall { get; set; }
    public long? Words { get; set; }
    public long? InputTokens { get; set; }
    public long? OutputTokens { get; set; }
    public long? LatencyMs { get; set; }
    public string? Error { get; set; }
    public string CreatedAt { get; set; } = "";
}

// Examples: khối bài mẫu đã dựng lúc tạo lượt, để chạy lại/nộp dùng đúng bộ đó
public sealed record RunSnapshot(string Prompt, PromptConfig Config, int Parallel, string? Examples = null);

public sealed record RunRequest(string? Name, long PromptId, string? Model, string? SetName, int? Parallel);

public sealed class RunStore(AppDb db)
{
    private const string RunColumns = """
        r.id AS Id, r.name AS Name, r.kind AS Kind, r.prompt_id AS PromptId, r.prompt_hash AS PromptHash, r.model AS Model,
        r.config AS Config, r.set_name AS SetName, r.parent_runs AS ParentRuns, r.status AS Status, r.error AS Error, r.score AS Score,
        r.precision AS Precision, r.recall AS Recall, r.avg_words AS AvgWords, r.input_tokens AS InputTokens,
        r.output_tokens AS OutputTokens, r.started_at AS StartedAt, r.finished_at AS FinishedAt,
        (SELECT json_array_length(paper_ids) FROM doc_sets WHERE name = r.set_name) AS Total,
        (SELECT COUNT(*) FROM outputs o WHERE o.run_id = r.id) AS Done,
        (SELECT COUNT(*) FROM outputs o WHERE o.run_id = r.id AND o.error IS NOT NULL) AS Errors
        """;

    public long Create(string name, PromptRow prompt, string model, string setName, RunSnapshot snapshot) =>
        Create(name, prompt.Id, prompt.Hash, model, setName, snapshot, null);

    // parentRun: lượt gốc khi chạy lại đúng cấu hình của nó trên tập khác (vd dev → test để nộp)
    public long Create(string name, long? promptId, string? promptHash, string model, string setName, RunSnapshot snapshot, long? parentRun)
    {
        using var conn = db.Open();
        return conn.ExecuteScalar<long>("""
            INSERT INTO runs (name, kind, prompt_id, prompt_hash, model, config, set_name, parent_runs, status, started_at)
            VALUES (@name, 'real', @promptId, @promptHash, @model, @config, @setName, @parents, 'running', @now) RETURNING id
            """, new
            {
                name, promptId, promptHash, model, config = JsonSerializer.Serialize(snapshot, PromptConfig.Json), setName,
                parents = parentRun is null ? null : JsonSerializer.Serialize(new[] { parentRun.Value }), now = Now(),
            });
    }

    public List<RunRow> List()
    {
        using var conn = db.Open();
        return conn.Query<RunRow>($"SELECT {RunColumns} FROM runs r ORDER BY r.id DESC").ToList();
    }

    public RunRow? Get(long id)
    {
        using var conn = db.Open();
        return conn.QuerySingleOrDefault<RunRow>($"SELECT {RunColumns} FROM runs r WHERE r.id = @id", new { id });
    }

    // withContext=false bỏ cột context (~16KB/bài) cho danh sách
    public List<OutputRow> Outputs(long runId, bool withContext = false)
    {
        using var conn = db.Open();
        string context = withContext ? "context_text" : "NULL";
        return conn.Query<OutputRow>($"""
            SELECT run_id AS RunId, paper_id AS PaperId, {context} AS ContextText, raw_output AS RawOutput, summary AS Summary,
                   score AS Score, precision AS Precision, recall AS Recall, words AS Words, input_tokens AS InputTokens,
                   output_tokens AS OutputTokens, latency_ms AS LatencyMs, error AS Error, created_at AS CreatedAt
            FROM outputs WHERE run_id = @runId ORDER BY paper_id
            """, new { runId }).ToList();
    }

    public OutputRow? Output(long runId, long paperId) =>
        Outputs(runId, withContext: true).FirstOrDefault(o => o.PaperId == paperId);

    public void SaveOutput(OutputRow o)
    {
        using var conn = db.Open();
        conn.Execute("""
            INSERT OR REPLACE INTO outputs (run_id, paper_id, context_text, raw_output, summary, score, precision, recall, words,
                                            input_tokens, output_tokens, latency_ms, error, created_at)
            VALUES (@RunId, @PaperId, @ContextText, @RawOutput, @Summary, @Score, @Precision, @Recall, @Words,
                    @InputTokens, @OutputTokens, @LatencyMs, @Error, @CreatedAt)
            """, o);
    }

    // Điểm = trung bình trên các bài chạy được; bài lỗi đếm riêng chứ không tính 0, để lỗi mạng không kéo điểm prompt xuống
    public void Finish(long runId, string status, string? error)
    {
        using var conn = db.Open();
        conn.Execute("""
            UPDATE runs SET status = @status, error = @error, finished_at = @now,
                score         = (SELECT AVG(score) FROM outputs WHERE run_id = @runId AND error IS NULL),
                precision     = (SELECT AVG(precision) FROM outputs WHERE run_id = @runId AND error IS NULL),
                recall        = (SELECT AVG(recall) FROM outputs WHERE run_id = @runId AND error IS NULL),
                avg_words     = (SELECT AVG(words) FROM outputs WHERE run_id = @runId AND error IS NULL),
                input_tokens  = (SELECT SUM(input_tokens) FROM outputs WHERE run_id = @runId),
                output_tokens = (SELECT SUM(output_tokens) FROM outputs WHERE run_id = @runId)
            WHERE id = @runId
            """, new { runId, status, error, now = Now() });
    }

    public void SetRunning(long runId)
    {
        using var conn = db.Open();
        conn.Execute("UPDATE runs SET status = 'running', error = NULL, finished_at = NULL WHERE id = @runId", new { runId });
    }

    public int MarkInterrupted()
    {
        using var conn = db.Open();
        return conn.Execute("UPDATE runs SET status = 'interrupted', finished_at = @now WHERE status = 'running'", new { now = Now() });
    }

    public bool Delete(long runId)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute("DELETE FROM outputs WHERE run_id = @runId", new { runId }, tx);
        int n = conn.Execute("DELETE FROM runs WHERE id = @runId", new { runId }, tx);
        tx.Commit();
        return n > 0;
    }

    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed record RunProgress(long RunId, int Total, int Done, int Errors, double? Score, bool Running);

public sealed class RunService(AppDb db, Prompts prompts, RunStore store, Summarizer summarizer, FewShot fewShot, IConfiguration config,
    IHostApplicationLifetime lifetime, ILogger<RunService> logger)
{
    private sealed class Active
    {
        public required CancellationTokenSource Cts;
        public required int Total;
        public int Done, Errors;
        public double ScoreSum;
        public int Scored;
    }

    private readonly ConcurrentDictionary<long, Active> _active = new();

    public string DefaultModel => config["OPENAI_MODEL"] ?? "gemini-3.8-flash-high";

    public RunProgress? Progress(long runId) =>
        _active.TryGetValue(runId, out var a)
            ? new(runId, a.Total, a.Done, a.Errors, a.Scored > 0 ? a.ScoreSum / a.Scored : null, true)
            : null;

    public bool IsRunning(long runId) => _active.ContainsKey(runId);

    public (long? RunId, string? Error) Start(RunRequest req)
    {
        if (prompts.Get(req.PromptId) is not { } prompt) return (null, $"No prompt #{req.PromptId}");
        string setName = req.SetName ?? "quick";
        if (!db.GetSets().ContainsKey(setName)) return (null, $"No set named {setName}");
        var cfg = PromptConfig.Parse(prompt.Config);
        if (Prompts.Validate(prompt.Content, cfg) is { } invalid) return (null, invalid);

        string model = string.IsNullOrWhiteSpace(req.Model) ? DefaultModel : req.Model.Trim();
        int parallel = Math.Clamp(req.Parallel ?? 4, 1, 16);
        string name = string.IsNullOrWhiteSpace(req.Name) ? $"#{prompt.Id} · {setName}" : req.Name.Trim();
        var snapshot = new RunSnapshot(prompt.Content, cfg, parallel, fewShot.Build(cfg));
        long runId = store.Create(name, prompt, model, setName, snapshot);
        Launch(runId, snapshot, model, db.GetSet(setName));
        return (runId, null);
    }

    // Chạy đúng prompt + cấu hình + model đã chụp ở lượt gốc trên một tập khác, kể cả khi prompt đã bị sửa sau đó
    public (long? RunId, string? Error) Rerun(long sourceRunId, string setName, string? name, int? parallel)
    {
        if (store.Get(sourceRunId) is not { Kind: "real" } source) return (null, $"No run #{sourceRunId}");
        if (!db.GetSets().ContainsKey(setName)) return (null, $"No set named {setName}");
        var snap = JsonSerializer.Deserialize<RunSnapshot>(source.Config, PromptConfig.Json)!;
        snap = snap with { Parallel = Math.Clamp(parallel ?? snap.Parallel, 1, 16) };
        string runName = string.IsNullOrWhiteSpace(name) ? $"{source.Name} → {setName}" : name.Trim();
        long runId = store.Create(runName, source.PromptId, source.PromptHash, source.Model!, setName, snap, source.Id);
        Launch(runId, snap, source.Model!, db.GetSet(setName));
        return (runId, null);
    }

    // Chạy lại bài lỗi và bài chưa có output (lượt bị ngắt), với đúng prompt/cấu hình đã chụp lúc tạo lượt
    public string? Retry(long runId)
    {
        if (IsRunning(runId)) return "Run is in progress";
        if (store.Get(runId) is not { Kind: "real" } run) return "No such run";
        var snapshot = JsonSerializer.Deserialize<RunSnapshot>(run.Config, PromptConfig.Json)!;
        var outputs = store.Outputs(run.Id).ToDictionary(o => o.PaperId);
        var todo = db.GetSet(run.SetName).Where(id => !outputs.TryGetValue(id, out var o) || o.Error is not null).ToList();
        if (todo.Count == 0) return "Nothing to retry";
        store.SetRunning(runId);
        Launch(runId, snapshot, run.Model!, todo);
        return null;
    }

    public bool Cancel(long runId)
    {
        if (!_active.TryGetValue(runId, out var a)) return false;
        a.Cts.Cancel();
        return true;
    }

    private void Launch(long runId, RunSnapshot snapshot, string model, List<long> paperIds)
    {
        var active = new Active { Cts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping), Total = paperIds.Count };
        _active[runId] = active;
        _ = Task.Run(() => ExecuteAsync(runId, snapshot, model, paperIds, active));
    }

    private async Task ExecuteAsync(long runId, RunSnapshot snapshot, string model, List<long> paperIds, Active active)
    {
        string status = "done";
        string? error = null;
        try
        {
            var docs = db.GetDocs(paperIds);
            var options = new ParallelOptions { MaxDegreeOfParallelism = snapshot.Parallel, CancellationToken = active.Cts.Token };
            await Parallel.ForEachAsync(docs, options, async (doc, ct) =>
            {
                var r = await summarizer.RunAsync(doc.Text, snapshot.Prompt, snapshot.Config, model, ct, snapshot.Examples);
                RougeScore? s = r.Summary is not null && doc.Summary is not null ? Rouge2.Score(r.Summary, doc.Summary) : null;
                store.SaveOutput(new OutputRow
                {
                    RunId = runId, PaperId = doc.PaperId, ContextText = r.Context, RawOutput = r.Raw, Summary = r.Summary,
                    Score = s?.F1, Precision = s?.Precision, Recall = s?.Recall, Words = r.Summary is null ? null : r.Words,
                    InputTokens = r.InputTokens, OutputTokens = r.OutputTokens, LatencyMs = r.LatencyMs, Error = r.Error,
                    CreatedAt = RunStore.Now(),
                });
                lock (active)
                {
                    active.Done++;
                    if (r.Error is not null) active.Errors++;
                    if (s is { } sc) { active.ScoreSum += sc.F1; active.Scored++; }
                }
            });
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Run {RunId} failed", runId);
            status = "failed";
            error = ex.Message;
        }
        finally
        {
            store.Finish(runId, status, error);
            _active.TryRemove(runId, out _);
            logger.LogInformation("Run {RunId} finished: {Status}", runId, status);
        }
    }

    public List<RunRow> List()
    {
        var hashes = prompts.List().ToDictionary(p => p.Id, p => p.Hash);
        var runs = store.List();
        foreach (var r in runs)
            r.Stale = r.PromptId is { } pid && r.PromptHash is not null && hashes.GetValueOrDefault(pid) != r.PromptHash ? "prompt edited since" : null;
        return runs;
    }
}

public sealed record CompareRow(long PaperId, double A, double B, double Delta);

public sealed record CompareResult(long A, long B, int Common, double? MeanA, double? MeanB, int Better, int Worse, int Same, List<CompareRow> Rows);

public static class Compare
{
    public static CompareResult Run(RunStore store, long a, long b)
    {
        var oa = store.Outputs(a).Where(o => o.Error is null && o.Score is not null).ToDictionary(o => o.PaperId, o => o.Score!.Value);
        var ob = store.Outputs(b).Where(o => o.Error is null && o.Score is not null).ToDictionary(o => o.PaperId, o => o.Score!.Value);
        var rows = oa.Keys.Intersect(ob.Keys).Select(id => new CompareRow(id, oa[id], ob[id], ob[id] - oa[id]))
            .OrderByDescending(r => r.Delta).ToList();
        const double tie = 0.005;
        return new(a, b, rows.Count,
            rows.Count > 0 ? rows.Average(r => r.A) : null,
            rows.Count > 0 ? rows.Average(r => r.B) : null,
            rows.Count(r => r.Delta > tie), rows.Count(r => r.Delta < -tie), rows.Count(r => Math.Abs(r.Delta) <= tie), rows);
    }
}
