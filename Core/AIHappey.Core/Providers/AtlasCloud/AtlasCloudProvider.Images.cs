using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AtlasCloud;

public partial class AtlasCloudProvider
{
    private const string AtlasCloudGenerateImageEndpoint = "https://api.atlascloud.ai/api/v1/model/generateImage";
    private const string AtlasCloudImageResultEndpointBase = "https://api.atlascloud.ai/api/v1/model/result/";
    private const string DefaultOutputMimeType = MediaTypeNames.Image.Png;

    private static readonly JsonSerializerOptions AtlasCloudImageJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AtlasCloudImageTaskResult(string? Id, string? Status, JsonElement Root);

    public async Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (request.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "AtlasCloud generateImage currently supports text-to-image only. Ignored files." });

        if (request.Mask is not null)
            warnings.Add(new { type = "unsupported", feature = "mask" });

        if (!string.IsNullOrWhiteSpace(request.Size))
            warnings.Add(new { type = "unsupported", feature = "size" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.N is > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "AtlasCloud endpoint returns provider-defined output count in this integration." });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["enable_base64_output"] = true
        };

        var json = JsonSerializer.Serialize(payload, AtlasCloudImageJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, AtlasCloudGenerateImageEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AtlasCloud image request failed ({(int)createResp.StatusCode}): {createRaw}");

        var task = ParseTaskResult(createRaw);

        if (!HasOutputs(task.Root))
        {
            if (string.IsNullOrWhiteSpace(task.Id))
                throw new InvalidOperationException("AtlasCloud image response contained no outputs and no request id for polling.");

            task = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => PollImageResultAsync(task.Id, ct),
                isTerminal: r => IsTerminalStatus(r.Status),
                interval: TimeSpan.FromSeconds(2),
                timeout: TimeSpan.FromMinutes(5),
                maxAttempts: null,
                cancellationToken: cancellationToken);
        }

        if (string.Equals(task.Status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AtlasCloud image task failed: {task.Root.GetRawText()}");

        var images = await ExtractImagesAsDataUrlsAsync(task.Root, cancellationToken);
        if (images.Count == 0)
            throw new InvalidOperationException("AtlasCloud image task completed but returned no images.");

        return new ImageResponse
        {
            Images = images,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = ResolveTimestamp(task.Root, now),
                ModelId = request.Model,
                Body = task.Root.Clone()
            }
        };
    }

    private async Task<AtlasCloudImageTaskResult> PollImageResultAsync(string requestId, CancellationToken cancellationToken)
    {
        var pollUrl = AtlasCloudImageResultEndpointBase + Uri.EscapeDataString(requestId);
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AtlasCloud image polling failed ({(int)pollResp.StatusCode}): {pollRaw}");

        return ParseTaskResult(pollRaw);
    }

    private static AtlasCloudImageTaskResult ParseTaskResult(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        return new AtlasCloudImageTaskResult(id, status, root);
    }

    private static bool IsTerminalStatus(string? status)
        => string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static bool HasOutputs(JsonElement root)
        => root.TryGetProperty("outputs", out var outputsEl)
           && outputsEl.ValueKind == JsonValueKind.Array
           && outputsEl.GetArrayLength() > 0;

    private async Task<List<string>> ExtractImagesAsDataUrlsAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("outputs", out var outputsEl) || outputsEl.ValueKind != JsonValueKind.Array)
            return [];

        List<string> images = [];
        foreach (var output in outputsEl.EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.String)
                continue;

            var value = output.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            images.Add(await NormalizeOutputToDataUrlAsync(value, cancellationToken));
        }

        return images;
    }

    private async Task<string> NormalizeOutputToDataUrlAsync(string output, CancellationToken cancellationToken)
    {
        var value = output.Trim();

        if (value.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            return value;

        if (IsHttpUrl(value))
            return await DownloadAsDataUrlAsync(value, cancellationToken);

        return value.ToDataUrl(DefaultOutputMimeType);
    }

    private async Task<string> DownloadAsDataUrlAsync(string url, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync(url, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AtlasCloud image download failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mediaType = resp.Content.Headers.ContentType?.MediaType
            ?? GuessImageMediaType(url)
            ?? DefaultOutputMimeType;

        return Convert.ToBase64String(bytes).ToDataUrl(mediaType);
    }

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static string? GuessImageMediaType(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;

            if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                return "image/webp";
            if (path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                return "image/jpeg";
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                return "image/svg+xml";
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                return "image/png";

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime ResolveTimestamp(JsonElement root, DateTime fallback)
    {
        if (root.TryGetProperty("created_at", out var createdAtEl)
            && createdAtEl.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(createdAtEl.GetString(), out var createdAt))
            return createdAt.UtcDateTime;

        return fallback;
    }
}

