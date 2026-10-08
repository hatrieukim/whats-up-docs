using System.ClientModel;
using System.Diagnostics;
using Microsoft.Agents.AI;
using OpenAI;
using OpenAI.Chat;
using WhatsUpDocs;

DotNetEnv.Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

string Path_(string key, string fallback) =>
    Path.GetFullPath(builder.Configuration[key] ?? fallback, builder.Environment.ContentRootPath);

string dataDir = Path_("DataDir", "../data");
builder.Services.AddSingleton(new AppDb(Path_("DbPath", "../data/app.db")));
builder.Services.AddSingleton(_ => new OpenAIClient(
    new ApiKeyCredential(Required("OPENAI_API_KEY")),
    new OpenAIClientOptions { Endpoint = new Uri(Required("OPENAI_BASE_URL")) }));
builder.Services.AddSingleton(sp => sp.GetRequiredService<OpenAIClient>().GetChatClient(Required("OPENAI_MODEL")));
builder.Services.AddSingleton<ILlm, MafLlm>();
builder.Services.AddSingleton<Summarizer>();
builder.Services.AddSingleton<Prompts>();
builder.Services.AddSingleton<FewShot>();
builder.Services.AddSingleton<RunStore>();
builder.Services.AddSingleton<RunService>();
builder.Services.AddSingleton(sp => new Submissions(sp.GetRequiredService<AppDb>(), sp.GetRequiredService<RunStore>(),
    Path.Combine(dataDir, "submission_format.csv"), Path.Combine(dataDir, "submissions")));

var app = builder.Build();

var db = app.Services.GetRequiredService<AppDb>();
var (train, test) = db.ImportIfEmpty(Path.Combine(dataDir, "train.csv"), Path.Combine(dataDir, "test_features.csv"));
if (train + test > 0) app.Logger.LogInformation("Imported {Train} train and {Test} test papers from {Dir}", train, test, dataDir);
if (db.EnsureSets() is > 0 and var sets) app.Logger.LogInformation("Created {Count} sets (split {Version})", sets, Splits.Version);
if (app.Services.GetRequiredService<Prompts>().SeedIfEmpty() is { } seeded) app.Logger.LogInformation("Seeded default prompt #{Id}", seeded);
// Lượt đang chạy dở khi app tắt: đánh dấu để người dùng bấm "chạy lại bài lỗi" (chạy nốt bài còn thiếu)
if (app.Services.GetRequiredService<RunStore>().MarkInterrupted() is > 0 and var stopped) app.Logger.LogWarning("{Count} runs were interrupted by shutdown", stopped);

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapGet("/api/status", () =>
{
    var (tr, te) = db.CountDocs();
    return new
    {
        docs = new { train = tr, test = te },
        sets = db.GetSets().ToDictionary(s => s.Key, s => s.Value.Count),
        model = Environment.GetEnvironmentVariable("OPENAI_MODEL"),
        cleanVersion = TextClean.Version,
        splitVersion = Splits.Version,
    };
});

app.MapGet("/api/sets", () => db.GetSets());

app.MapGet("/api/docs", () =>
{
    var membership = new Dictionary<long, string>();
    foreach (var name in new[] { "test", "pool", "holdout", "dev", "quick" })
        foreach (long id in db.GetSets().GetValueOrDefault(name, []))
            membership[id] = name;
    return db.ListDocs().Select(d => new { d.PaperId, d.Split, set = membership.GetValueOrDefault(d.PaperId), d.Chars, d.SummaryWords });
});

// view: raw | clean | context; mode áp dụng cho context
app.MapGet("/api/docs/{id:long}", (long id, string? view, string? mode) =>
{
    if (db.GetDoc(id) is not { } doc) return Results.NotFound();
    if (!Enum.TryParse<ContextMode>(mode ?? "Sections", ignoreCase: true, out var m)) return Results.BadRequest("mode = sections | full | numbered");
    string text = (view ?? "raw") switch
    {
        "raw" => doc.Text,
        "clean" => TextClean.Clean(doc.Text, keepHeaders: true),
        "context" => ContextBuilder.Build(doc.Text, new ContextOptions(m)).Text,
        _ => "",
    };
    if (text == "") return Results.BadRequest("view = raw | clean | context");
    return Results.Ok(new { doc.PaperId, doc.Split, doc.Summary, view, text, chars = text.Length, approxTokens = text.Length / ContextBuilder.CharsPerToken });
});

app.MapGet("/api/baseline/cue", (string? set) =>
{
    var sw = Stopwatch.StartNew();
    var rows = db.GetDocs(db.GetSet(set ?? "dev")).Where(d => d.Summary is not null)
        .Select(d => (d.PaperId, score: Rouge2.Score(CueExtract.Run(d.Text), d.Summary!))).ToList();
    if (rows.Count == 0) return Results.BadRequest("This set has no reference abstracts");
    return Results.Ok(new
    {
        set = set ?? "dev",
        docs = rows.Count,
        score = rows.Average(r => r.score.F1),
        precision = rows.Average(r => r.score.Precision),
        recall = rows.Average(r => r.score.Recall),
        ms = sw.ElapsedMilliseconds,
        perDoc = rows.ToDictionary(r => r.PaperId, r => r.score.F1),
    });
});

app.MapPromptApi();
app.MapRunApi();
app.MapSubmissionApi();

// Kiểm tra đường MAF → CLIProxyAPI → model
app.MapGet("/api/llm/ping", async (ChatClient chat, CancellationToken ct) =>
{
    var sw = Stopwatch.StartNew();
    var agent = chat.AsAIAgent(name: "ping", instructions: "Reply with exactly one word: pong");
    var response = await agent.RunAsync("ping", cancellationToken: ct);
    return new
    {
        model = Environment.GetEnvironmentVariable("OPENAI_MODEL"),
        text = response.Text,
        inputTokens = response.Usage?.InputTokenCount,
        outputTokens = response.Usage?.OutputTokenCount,
        ms = sw.ElapsedMilliseconds,
    };
});

app.Run();

static string Required(string name) =>
    Environment.GetEnvironmentVariable(name) ?? throw new InvalidOperationException($"Missing {name} in .env");

public partial class Program;
