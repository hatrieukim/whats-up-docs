using System.Text.Json;

namespace WhatsUpDocs;

public static class Sse
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Start(HttpResponse response)
    {
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
    }

    public static async Task WriteAsync(HttpResponse response, string evt, object data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {evt}\ndata: {JsonSerializer.Serialize(data, Json)}\n\n", ct);
        await response.Body.FlushAsync(ct);
    }
}
