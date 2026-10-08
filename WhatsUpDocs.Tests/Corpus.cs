using System.Text.Json;
using WhatsUpDocs;

namespace WhatsUpDocs.Tests;

// DB tạm nạp từ CSV thật, dùng chung cho mọi test cần dữ liệu (nạp ~58MB một lần)
public sealed class Corpus : IDisposable
{
    public static readonly string Root = FindRoot();
    public static string Data(string name) => Path.Combine(Root, "data", name);
    public static string Fixture(string name) => Path.Combine(Root, "data", "fixtures", name);

    public string DbPath { get; } = Path.Combine(Path.GetTempPath(), $"whatsupdocs-test-{Guid.NewGuid():N}.db");
    public AppDb Db { get; }
    public (int Train, int Test) Imported { get; }

    public Corpus()
    {
        Db = new AppDb(DbPath);
        Imported = Db.ImportIfEmpty(Data("train.csv"), Data("test_features.csv"));
        Db.EnsureSets();
    }

    public static T ReadJson<T>(string fixture) => JsonSerializer.Deserialize<T>(File.ReadAllText(Fixture(fixture)))!;

    public static IEnumerable<JsonElement> ReadJsonl(string fixture) =>
        File.ReadLines(Fixture(fixture)).Select(l => JsonDocument.Parse(l).RootElement);

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "WhatsUpDocs.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("Không tìm thấy WhatsUpDocs.sln");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var f in new[] { DbPath, DbPath + "-wal", DbPath + "-shm" }) File.Delete(f);
    }
}

[CollectionDefinition("corpus")]
public sealed class CorpusCollection : ICollectionFixture<Corpus>;
