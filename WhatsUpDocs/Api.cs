using OpenAI;

namespace WhatsUpDocs;

public sealed record PromptInput(string? Content, string? Note, PromptConfig? Config);

public sealed record TryInput(string? Content, PromptConfig? Config, List<long>? PaperIds, string? Model);

public static class Api
{
    public static void MapPromptApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/prompts");

        // Kèm điểm lượt xong gần nhất theo từng tập, chỉ tính lượt chạy đúng nội dung hiện tại của bản đó
        g.MapGet("", (Prompts prompts, RunService runs) =>
        {
            var done = runs.List().Where(r => r.Status == "done" && r.Stale is null).ToList();
            return prompts.List().Select(p => new
            {
                p.Id, p.Content, p.Note, Config = PromptConfig.Parse(p.Config), p.CreatedAt, p.Hash,
                Scores = done.Where(r => r.PromptId == p.Id).GroupBy(r => r.SetName)
                    .ToDictionary(x => x.Key, x => x.First().Score),
            });
        });

        g.MapPost("", (PromptInput body, Prompts prompts) =>
        {
            var cfg = (body.Config ?? new PromptConfig()).Normalized();
            if (Prompts.Validate(body.Content ?? "", cfg) is { } err) return Results.BadRequest(err);
            return Results.Ok(new { id = prompts.Create(body.Content!, body.Note, cfg) });
        });

        g.MapPut("{id:long}", (long id, PromptInput body, Prompts prompts) =>
        {
            var cfg = (body.Config ?? new PromptConfig()).Normalized();
            if (Prompts.Validate(body.Content ?? "", cfg) is { } err) return Results.BadRequest(err);
            return prompts.Update(id, body.Content!, body.Note, cfg) ? Results.Ok(new { id }) : Results.NotFound();
        });

        // Thử bản đang soạn (chưa cần lưu) trên vài bài, không ghi DB
        app.MapPost("/api/try", async (TryInput body, AppDb db, Summarizer summarizer, FewShot fewShot, RunService runs, CancellationToken ct) =>
        {
            var cfg = (body.Config ?? new PromptConfig()).Normalized();
            if (Prompts.Validate(body.Content ?? "", cfg) is { } err) return Results.BadRequest(err);
            if (body.PaperIds is not { Count: > 0 and <= 10 } ids) return Results.BadRequest("Pick 1–10 papers");
            string model = string.IsNullOrWhiteSpace(body.Model) ? runs.DefaultModel : body.Model;
            var docs = db.GetDocs(ids);
            string? examples = fewShot.Build(cfg);
            var results = await Task.WhenAll(docs.Select(async d =>
            {
                var r = await summarizer.RunAsync(d.Text, body.Content!, cfg, model, ct, examples);
                RougeScore? s = r.Summary is not null && d.Summary is not null ? Rouge2.Score(r.Summary, d.Summary) : null;
                return new
                {
                    d.PaperId, Reference = d.Summary, r.Summary, r.Raw, r.Prompt, r.Picked, r.Used, r.Words,
                    r.InputTokens, r.OutputTokens, r.LatencyMs, r.Error,
                    Score = s?.F1, s?.Precision, s?.Recall,
                };
            }));
            return Results.Ok(new { model, results });
        });

        // Xem trước bộ bài mẫu sẽ chèn vào {{examples}} với cấu hình đang soạn
        app.MapPost("/api/examples", (PromptConfig body, FewShot fewShot) =>
        {
            var cfg = body.Normalized();
            if (cfg.Validate() is { } err) return Results.BadRequest(err);
            if (cfg.Examples is null) return Results.BadRequest("No example mode selected");
            var picked = fewShot.Pick(cfg);
            string text = FewShot.Render(cfg.Examples, picked);
            return Results.Ok(new
            {
                docs = picked.Select(e => new { e.PaperId, e.OracleF1, e.ContextTokens, e.Answer }),
                text, approxTokens = text.Length / ContextBuilder.CharsPerToken,
            });
        });

        app.MapGet("/api/models", async (OpenAIClient client, RunService runs, CancellationToken ct) =>
        {
            try
            {
                var models = await client.GetOpenAIModelClient().GetModelsAsync(ct);
                return Results.Ok(new { @default = runs.DefaultModel, models = models.Value.Select(m => m.Id).Order() });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { @default = runs.DefaultModel, models = new[] { runs.DefaultModel }, error = ex.Message });
            }
        });
    }

    public static void MapRunApi(this WebApplication app)
    {
        var g = app.MapGroup("/api/runs");

        g.MapGet("", (RunService runs) => runs.List().Select(r => Decorate(r, runs)));

        g.MapPost("", (RunRequest req, RunService runs) =>
            runs.Start(req) switch
            {
                (long id, null) => Results.Ok(new { id }),
                var (_, err) => Results.BadRequest(err),
            });

        g.MapGet("{id:long}", (long id, RunService runs, RunStore store) =>
            runs.List().FirstOrDefault(r => r.Id == id) is { } run
                ? Results.Ok(new { run = Decorate(run, runs), outputs = store.Outputs(id) })
                : Results.NotFound());

        // Một bài trong một lượt: kèm abstract tham chiếu để tô màu
        g.MapGet("{id:long}/outputs/{paperId:long}", (long id, long paperId, RunStore store, AppDb db) =>
            store.Output(id, paperId) is { } o
                ? Results.Ok(new { output = o, reference = db.GetDoc(paperId)?.Summary })
                : Results.NotFound());

        g.MapPost("{id:long}/cancel", (long id, RunService runs) => runs.Cancel(id) ? Results.Ok() : Results.NotFound());

        g.MapPost("{id:long}/retry", (long id, RunService runs) =>
            runs.Retry(id) is { } err ? Results.BadRequest(err) : Results.Ok());

        g.MapDelete("{id:long}", (long id, RunService runs, RunStore store) =>
            runs.IsRunning(id) ? Results.Conflict("Run is in progress, cancel it first") : store.Delete(id) ? Results.Ok() : Results.NotFound());

        // Tiến độ mỗi giây cho tới khi lượt dừng; EventSource tự nối lại nếu rớt mạng
        g.MapGet("{id:long}/events", async (long id, RunService runs, RunStore store, HttpContext http, CancellationToken ct) =>
        {
            Sse.Start(http.Response);
            try
            {
                while (!ct.IsCancellationRequested && runs.Progress(id) is { } p)
                {
                    await Sse.WriteAsync(http.Response, "progress", p, ct);
                    await Task.Delay(1000, ct);
                }
                if (!ct.IsCancellationRequested)
                    await Sse.WriteAsync(http.Response, "done", (object?)store.Get(id) ?? new { id }, ct);
            }
            catch (OperationCanceledException) { }
        });

        g.MapGet("{id:long}/diagnose", (long id, RunStore store, AppDb db) =>
            store.Get(id) is { } run ? Results.Ok(Diagnostics.Run(run, store, db)) : Results.NotFound());

        app.MapGet("/api/compare", (long a, long b, RunStore store) => Compare.Run(store, a, b));
    }

    private static object Decorate(RunRow r, RunService runs) => new
    {
        r.Id, r.Name, r.Kind, r.PromptId, r.ParentRuns, r.Model, r.SetName, r.Status, r.Error, r.Score, r.Precision, r.Recall,
        r.AvgWords, r.InputTokens, r.OutputTokens, r.StartedAt, r.FinishedAt, r.Total, r.Done, r.Errors, r.Stale,
        Progress = runs.Progress(r.Id),
    };
}

public sealed record RerunInput(long SourceRunId, string? SetName, string? Name, int? Parallel);
public sealed record SubmitInput(long RunId, string? Note);

public static class SubmissionApi
{
    public static void MapSubmissionApi(this WebApplication app)
    {
        app.MapPost("/api/runs/rerun", (RerunInput body, RunService runs) =>
            runs.Rerun(body.SourceRunId, body.SetName ?? "test", body.Name, body.Parallel) switch
            {
                (long id, null) => Results.Ok(new { id }),
                var (_, err) => Results.BadRequest(err),
            });

        var g = app.MapGroup("/api/submissions");
        g.MapGet("", (Submissions s) => s.List());
        g.MapGet("check/{runId:long}", (long runId, Submissions s) => s.Check(runId));
        g.MapPost("", (SubmitInput body, Submissions s) =>
            s.Create(body.RunId, body.Note) switch
            {
                ({ } row, null) => Results.Ok(row),
                var (_, err) => Results.BadRequest(err),
            });
        g.MapPut("{id:long}", (long id, SubmissionUpdate body, Submissions s) => s.Update(id, body) ? Results.Ok() : Results.NotFound());
        g.MapGet("{id:long}/download", (long id, Submissions s) =>
            s.Get(id) is { } row && File.Exists(row.FilePath)
                ? Results.File(row.FilePath, "text/csv", Path.GetFileName(row.FilePath))
                : Results.NotFound());
    }
}
