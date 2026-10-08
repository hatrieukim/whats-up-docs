using System.Globalization;
using System.Text.Json;
using CsvHelper;
using Dapper;
using Microsoft.Data.Sqlite;

namespace WhatsUpDocs;

public sealed record DocRow(long PaperId, string Split, string Text, string? Summary, long Chars);

public sealed record DocListItem(long PaperId, string Split, long Chars, long? SummaryWords);

public sealed class AppDb
{
    private readonly string _connectionString;

    public AppDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = dbPath, ForeignKeys = true }.ToString();
        using var conn = Open();
        conn.Execute("""
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS docs (
                paper_id INTEGER PRIMARY KEY,
                split    TEXT NOT NULL CHECK (split IN ('train', 'test')),
                text     TEXT NOT NULL,
                summary  TEXT,
                chars    INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS doc_sets (
                name       TEXT PRIMARY KEY,
                version    TEXT NOT NULL,
                paper_ids  TEXT NOT NULL,
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS prompts (
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                content    TEXT NOT NULL,
                note       TEXT,
                config     TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS runs (
                id            INTEGER PRIMARY KEY AUTOINCREMENT,
                name          TEXT NOT NULL,
                kind          TEXT NOT NULL CHECK (kind IN ('real', 'virtual')),
                prompt_id     INTEGER REFERENCES prompts(id),
                prompt_hash   TEXT,
                model         TEXT,
                config        TEXT NOT NULL DEFAULT '{}',
                set_name      TEXT NOT NULL,
                parent_runs   TEXT,
                status        TEXT NOT NULL,
                error         TEXT,
                score         REAL,
                precision     REAL,
                recall        REAL,
                avg_words     REAL,
                input_tokens  INTEGER,
                output_tokens INTEGER,
                started_at    TEXT NOT NULL,
                finished_at   TEXT
            );
            CREATE TABLE IF NOT EXISTS outputs (
                run_id        INTEGER NOT NULL REFERENCES runs(id) ON DELETE CASCADE,
                paper_id      INTEGER NOT NULL REFERENCES docs(paper_id),
                context_text  TEXT,
                raw_output    TEXT,
                summary       TEXT,
                score         REAL,
                precision     REAL,
                recall        REAL,
                words         INTEGER,
                input_tokens  INTEGER,
                output_tokens INTEGER,
                latency_ms    INTEGER,
                error         TEXT,
                created_at    TEXT NOT NULL,
                PRIMARY KEY (run_id, paper_id)
            );
            CREATE TABLE IF NOT EXISTS submissions (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id       INTEGER NOT NULL REFERENCES runs(id),
                file_path    TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                public_score REAL,
                note         TEXT
            );
            """);
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return conn;
    }

    // Chỉ nhập khi bảng docs còn trống: CSV của BTC không đổi, nhập lại không có ích
    public (int Train, int Test) ImportIfEmpty(string trainCsv, string testCsv)
    {
        using var conn = Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM docs") > 0) return (0, 0);

        using var tx = conn.BeginTransaction();
        int train = 0, test = 0;
        foreach (var (id, text, summary) in ReadCsv(trainCsv, withSummary: true))
        {
            Insert(conn, tx, id, "train", text, summary);
            train++;
        }
        foreach (var (id, text, _) in ReadCsv(testCsv, withSummary: false))
        {
            Insert(conn, tx, id, "test", text, null);
            test++;
        }
        tx.Commit();
        return (train, test);
    }

    private static void Insert(SqliteConnection conn, SqliteTransaction tx, long id, string split, string text, string? summary) =>
        conn.Execute("INSERT INTO docs (paper_id, split, text, summary, chars) VALUES (@id, @split, @text, @summary, @chars)",
            new { id, split, text, summary, chars = text.Length }, tx);

    private static IEnumerable<(long Id, string Text, string? Summary)> ReadCsv(string path, bool withSummary)
    {
        using var reader = new StreamReader(path);
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        while (csv.Read())
            yield return (csv.GetField<long>("paper_id"), csv.GetField("text")!, withSummary ? csv.GetField("summary") : null);
    }

    // Tập đã lưu thì giữ nguyên (kể cả khi code chia tập đổi) để điểm các lượt cũ còn so được
    public int EnsureSets()
    {
        using var conn = Open();
        if (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM doc_sets") > 0) return 0;
        var train = conn.Query<long>("SELECT paper_id FROM docs WHERE split = 'train'");
        var test = conn.Query<long>("SELECT paper_id FROM docs WHERE split = 'test'");
        var sets = Splits.Make(train, test);
        string now = DateTimeOffset.UtcNow.ToString("O");
        using var tx = conn.BeginTransaction();
        foreach (var (name, ids) in sets)
            conn.Execute("INSERT INTO doc_sets (name, version, paper_ids, created_at) VALUES (@name, @version, @ids, @now)",
                new { name, version = Splits.Version, ids = JsonSerializer.Serialize(ids), now }, tx);
        tx.Commit();
        return sets.Count;
    }

    public Dictionary<string, List<long>> GetSets()
    {
        using var conn = Open();
        return conn.Query<(string Name, string Ids)>("SELECT name, paper_ids FROM doc_sets")
            .ToDictionary(r => r.Name, r => JsonSerializer.Deserialize<List<long>>(r.Ids)!);
    }

    public List<long> GetSet(string name) =>
        GetSets().TryGetValue(name, out var ids) ? ids : throw new KeyNotFoundException($"No set named {name}");

    public DocRow? GetDoc(long paperId)
    {
        using var conn = Open();
        return conn.QuerySingleOrDefault<DocRow>("""
            SELECT paper_id AS PaperId, split AS Split, text AS Text, summary AS Summary, chars AS Chars
            FROM docs WHERE paper_id = @paperId
            """, new { paperId });
    }

    public List<DocRow> GetDocs(IEnumerable<long> ids)
    {
        using var conn = Open();
        var rows = conn.Query<DocRow>("""
            SELECT paper_id AS PaperId, split AS Split, text AS Text, summary AS Summary, chars AS Chars
            FROM docs WHERE paper_id IN (SELECT value FROM json_each(@ids))
            """, new { ids = JsonSerializer.Serialize(ids) }).ToDictionary(d => d.PaperId);
        return ids.Where(rows.ContainsKey).Select(id => rows[id]).ToList();
    }

    public List<DocListItem> ListDocs()
    {
        using var conn = Open();
        // Số từ đếm thô theo dấu cách, chỉ để hiển thị
        return conn.Query<DocListItem>("""
            SELECT paper_id AS PaperId, split AS Split, chars AS Chars,
                   CASE WHEN summary IS NULL THEN NULL
                        ELSE length(trim(summary)) - length(replace(trim(summary), ' ', '')) + 1 END AS SummaryWords
            FROM docs ORDER BY paper_id
            """).ToList();
    }

    public (long Train, long Test) CountDocs()
    {
        using var conn = Open();
        return (conn.ExecuteScalar<long>("SELECT COUNT(*) FROM docs WHERE split = 'train'"),
                conn.ExecuteScalar<long>("SELECT COUNT(*) FROM docs WHERE split = 'test'"));
    }
}
