using System.Security.Cryptography;
using System.Text;

namespace WhatsUpDocs;

public static class Splits
{
    public const string Version = "v1";
    public const int Dev = 150, Quick = 30, Holdout = 100;
    public static readonly string[] Names = ["quick", "dev", "holdout", "pool", "test"];

    // Sắp theo SHA-256 của id thay vì Random(seed): không phụ thuộc thứ tự nạp CSV, Python tái lập được y hệt
    public static string SortKey(long paperId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"whatsupdocs-split-{Version}:{paperId}"))).ToLowerInvariant();

    // quick ⊂ dev; dev, holdout, pool rời nhau và phủ hết train
    public static Dictionary<string, List<long>> Make(IEnumerable<long> trainIds, IEnumerable<long> testIds)
    {
        var ordered = trainIds.OrderBy(SortKey, StringComparer.Ordinal).ToList();
        if (ordered.Count < Dev + Holdout) throw new InvalidOperationException($"Train has only {ordered.Count} papers");
        var dev = ordered.Take(Dev).ToList();
        return new()
        {
            ["quick"] = dev.Take(Quick).Order().ToList(),
            ["dev"] = dev.Order().ToList(),
            ["holdout"] = ordered.Skip(Dev).Take(Holdout).Order().ToList(),
            ["pool"] = ordered.Skip(Dev + Holdout).Order().ToList(),
            ["test"] = testIds.Order().ToList(),
        };
    }
}
