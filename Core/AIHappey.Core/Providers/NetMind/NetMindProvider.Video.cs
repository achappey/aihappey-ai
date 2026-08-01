using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.NetMind;

public partial class NetMindProvider
{
    private static readonly Uri NetMindGenerationUri = new("https://api.netmind.ai/v1/generation");

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model)) throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt)) throw new ArgumentException("Prompt is required.", nameof(request));
        ApplyAuthHeader();
        var payload = NetMindObject(request.GetProviderMetadata<JsonElement>(GetIdentifier()));
        payload["model"] = request.Model; payload["prompt"] = request.Prompt; payload["duration"] = request.Duration;
        payload["resolution"] = request.Resolution; payload["aspect_ratio"] = request.AspectRatio; payload["fps"] = request.Fps; payload["seed"] = request.Seed; payload["n"] = request.N;
        if (request.Image is not null) payload["image"] = request.Image.Data;

        using var createRequest = new HttpRequestMessage(HttpMethod.Post, NetMindGenerationUri)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(createRequest, cancellationToken);
        var createRaw = await createResponse.Content.ReadAsStringAsync(cancellationToken);
        if (!createResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"NetMind video create failed ({(int)createResponse.StatusCode}): {createRaw}");
        using var createDoc = JsonDocument.Parse(createRaw); var create = createDoc.RootElement.Clone();
        var id = FindNetMindString(create, "id", "task_id", "job_id", "generation_id")
        ?? throw new InvalidOperationException("NetMind video create response contained no job id.");

        var completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(Poll, x => NetMindTerminal(FindNetMindString(x, "status", "state")), TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(20), null, cancellationToken);
        var status = FindNetMindString(completed, "status", "state");
        if (!NetMindSuccess(status))
            throw new InvalidOperationException($"NetMind video job '{id}' failed with status '{status ?? "unknown"}': {completed.GetRawText()}");
        var urls = FindNetMindUrls(completed).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (urls.Count == 0)
            throw new InvalidOperationException("NetMind video job completed without result URLs.");
        var videos = new List<VideoResponseFile>();
        foreach (var url in urls)
        {
            using var download = await _client.GetAsync(url, cancellationToken); var bytes = await download.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!download.IsSuccessStatusCode || bytes.Length == 0) throw new InvalidOperationException($"NetMind video result download failed ({(int)download.StatusCode}).");
            videos.Add(new()
            {
                Data = Convert.ToBase64String(bytes),
                MediaType = download.Content.Headers.ContentType?.MediaType ?? "video/mp4"
            });
        }
        using var delete = await _client.DeleteAsync(new Uri($"{NetMindGenerationUri}/{Uri.EscapeDataString(id)}"), cancellationToken);
        if (!delete.IsSuccessStatusCode) throw new InvalidOperationException($"NetMind video job cleanup failed ({(int)delete.StatusCode}).");
        return new VideoResponse
        {
            Videos = videos,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { create, poll = completed }),
            Response = new() { Timestamp = DateTime.UtcNow, Headers = createResponse.GetHeaders(), ModelId = request.Model.ToModelId(GetIdentifier()) }
        };

        async Task<JsonElement> Poll(CancellationToken token)
        { using var response = await _client.GetAsync(new Uri($"{NetMindGenerationUri}/{Uri.EscapeDataString(id)}"), token); var raw = await response.Content.ReadAsStringAsync(token); if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NetMind video poll failed ({(int)response.StatusCode}): {raw}"); using var doc = JsonDocument.Parse(raw); return doc.RootElement.Clone(); }
    }

    private static bool NetMindTerminal(string? s) => NetMindSuccess(s) || s?.ToLowerInvariant() is "failed" or "error" or "cancelled" or "canceled";
    private static bool NetMindSuccess(string? s) => s?.ToLowerInvariant() is "success" or "succeeded" or "completed" or "done";
    private static string? FindNetMindString(JsonElement e, params string[] names)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var n in names)
                if (e.TryGetProperty(n, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number) return v.ToString(); foreach (var p in e.EnumerateObject()) { var found = FindNetMindString(p.Value, names); if (found is not null) return found; }
        }
        return null;
    }
    private static IEnumerable<string> FindNetMindUrls(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.String && Uri.TryCreate(e.GetString(), UriKind.Absolute, out var uri) && (uri.AbsolutePath.Contains("video", StringComparison.OrdinalIgnoreCase) || uri.AbsolutePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))) yield return uri.ToString(); else if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) foreach (var u in FindNetMindUrls(p.Value)) yield return u; else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) foreach (var u in FindNetMindUrls(item)) yield return u;
    }
}
