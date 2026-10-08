using System.Globalization;
using CsvHelper;
using Dapper;

namespace WhatsUpDocs;

public sealed record SubmissionCheck(
    long RunId, int Expected, int Ready, int Errors, int Missing, double? AvgWords, long? MinWords, long? MaxWords,
    string? Problem)
{
    public bool Ok => Problem is null;
}

public sealed class SubmissionRow
{
    public long Id { get; set; }
    public long RunId { get; set; }
    public string FilePath { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public double? PublicScore { get; set; }
    public string? Note { get; set; }
    public string RunName { get; set; } = "";
    public long? PromptId { get; set; }
    public string? Model { get; set; }
    public string? ParentRuns { get; set; }
    // Điểm dev của lượt gốc (lượt test không có abstract nên không tự chấm được)
    public double? SourceScore { get; set; }
    public string? SourceSet { get; set; }
}

public sealed record SubmissionUpdate(double? PublicScore, string? Note);

// formatPath: file mẫu của BTC; outputDir: nơi ghi file CSV nộp
public sealed class Submissions(AppDb db, RunStore runs, string formatPath, string outputDir)
{
    public string FormatPath => formatPath;
    public string OutputDir => outputDir;

    // Thứ tự và danh sách id lấy từ file mẫu của BTC, không lấy từ DB, để chắc khớp đúng file họ chấm
    public List<long> ExpectedIds() =>
        File.ReadLines(FormatPath).Skip(1).Where(l => l.Length > 0).Select(l => long.Parse(l.Split(',')[0], CultureInfo.InvariantCulture)).ToList();

    public SubmissionCheck Check(long runId)
    {
        var expected = ExpectedIds();
        var outputs = runs.Outputs(runId).ToDictionary(o => o.PaperId);
        var ready = expected.Where(id => outputs.TryGetValue(id, out var o) && o.Error is null && !string.IsNullOrWhiteSpace(o.Summary)).ToList();
        int errors = expected.Count(id => outputs.TryGetValue(id, out var o) && o.Error is not null);
        int missing = expected.Count(id => !outputs.ContainsKey(id));
        var words = ready.Select(id => outputs[id].Words ?? 0).ToList();
        string? problem = runs.Get(runId) is not { } run ? "No such run"
            : run.Status == "running" ? "Run is in progress"
            : errors > 0 || missing > 0 ? $"{errors} papers failed and {missing} not run yet: click \"Retry failed\" first"
            : ready.Count != expected.Count ? $"{expected.Count - ready.Count} papers have an empty output"
            : null;
        return new(runId, expected.Count, ready.Count, errors, missing,
            words.Count > 0 ? words.Average() : null, words.Count > 0 ? words.Min() : null, words.Count > 0 ? words.Max() : null, problem);
    }

    public (SubmissionRow? Row, string? Error) Create(long runId, string? note)
    {
        var check = Check(runId);
        if (!check.Ok) return (null, check.Problem);
        var outputs = runs.Outputs(runId).ToDictionary(o => o.PaperId);
        Directory.CreateDirectory(OutputDir);
        string file = Path.Combine(OutputDir, $"submission-run{runId}-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        WriteCsv(file, ExpectedIds().Select(id => (id, outputs[id].Summary!)));

        using var conn = db.Open();
        long id = conn.ExecuteScalar<long>("INSERT INTO submissions (run_id, file_path, created_at, note) VALUES (@runId, @file, @now, @note) RETURNING id",
            new { runId, file, now = RunStore.Now(), note = string.IsNullOrWhiteSpace(note) ? null : note.Trim() });
        return (List().First(s => s.Id == id), null);
    }

    public static void WriteCsv(string path, IEnumerable<(long PaperId, string Summary)> rows)
    {
        using var writer = new StreamWriter(path, false, new System.Text.UTF8Encoding(false));
        using var csv = new CsvWriter(writer, CultureInfo.InvariantCulture);
        csv.WriteField("paper_id");
        csv.WriteField("summary");
        csv.NextRecord();
        foreach (var (id, summary) in rows)
        {
            csv.WriteField(id);
            // Dòng mới trong một ô hợp lệ với CSV nhưng dễ làm hỏng trình đọc sơ sài, nên gộp thành dấu cách
            csv.WriteField(summary.Replace("\r", " ").Replace("\n", " ").Trim());
            csv.NextRecord();
        }
    }

    public List<SubmissionRow> List()
    {
        using var conn = db.Open();
        return conn.Query<SubmissionRow>("""
            SELECT s.id AS Id, s.run_id AS RunId, s.file_path AS FilePath, s.created_at AS CreatedAt, s.public_score AS PublicScore,
                   s.note AS Note, r.name AS RunName, r.prompt_id AS PromptId, r.model AS Model, r.parent_runs AS ParentRuns,
                   p.score AS SourceScore, p.set_name AS SourceSet
            FROM submissions s JOIN runs r ON r.id = s.run_id
            LEFT JOIN runs p ON p.id = json_extract(r.parent_runs, '$[0]')
            ORDER BY s.id DESC
            """).ToList();
    }

    public bool Update(long id, SubmissionUpdate u)
    {
        using var conn = db.Open();
        return conn.Execute("UPDATE submissions SET public_score = @PublicScore, note = @Note WHERE id = @id",
            new { id, u.PublicScore, Note = string.IsNullOrWhiteSpace(u.Note) ? null : u.Note.Trim() }) > 0;
    }

    public SubmissionRow? Get(long id) => List().FirstOrDefault(s => s.Id == id);
}
