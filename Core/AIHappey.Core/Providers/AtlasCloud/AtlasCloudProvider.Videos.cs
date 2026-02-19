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
    private const string AtlasCloudGenerateVideoEndpoint = "https://api.atlascloud.ai/api/v1/model/generateVideo";
    private const string AtlasCloudVideoResultEndpointBase = "https://api.atlascloud.ai/api/v1/model/result/";
    private const string DefaultVideoMimeType = "video/mp4";

    private static readonly JsonSerializerOptions AtlasCloudVideoJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record AtlasCloudVideoTaskResult(string? Id, string? Status, JsonElement Root);

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n", details = "AtlasCloud endpoint returns provider-defined output count in this integration." });

        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = string.IsNullOrWhiteSpace(request.Prompt) ? null : request.Prompt,
            ["duration"] = request.Duration,
            ["resolution"] = string.IsNullOrWhiteSpace(request.Resolution) ? null : request.Resolution,
            ["aspect_ratio"] = string.IsNullOrWhiteSpace(request.AspectRatio) ? null : request.AspectRatio,
            ["seed"] = request.Seed
        };

        if (request.Image is not null)
            payload["image"] = NormalizeVideoImageInput(request.Image);

        MergeProviderOptions(payload, request, GetIdentifier());

        var json = JsonSerializer.Serialize(payload, AtlasCloudVideoJson);
        using var createReq = new HttpRequestMessage(HttpMethod.Post, AtlasCloudGenerateVideoEndpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var createResp = await _client.SendAsync(createReq, cancellationToken);
        var createRaw = await createResp.Content.ReadAsStringAsync(cancellationToken);
        if (!createResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AtlasCloud video request failed ({(int)createResp.StatusCode}): {createRaw}");

        var createTask = ParseVideoTaskResult(createRaw);
        var finalTask = createTask;

        if (!HasOutputs(finalTask.Root))
        {
            if (string.IsNullOrWhiteSpace(finalTask.Id))
                throw new InvalidOperationException("AtlasCloud video response contained no outputs and no request id for polling.");

            finalTask = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => PollVideoResultAsync(finalTask.Id, ct),
                isTerminal: r => IsTerminalStatus(r.Status),
                interval: TimeSpan.FromSeconds(2),
                timeout: TimeSpan.FromMinutes(5),
                maxAttempts: null,
                cancellationToken: cancellationToken);
        }

        if (string.Equals(finalTask.Status, "failed", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AtlasCloud video task failed: {finalTask.Root.GetRawText()}");

        var videos = await ExtractVideosAsync(finalTask.Root, cancellationToken);
        if (videos.Count == 0)
            throw new InvalidOperationException("AtlasCloud video task completed but returned no videos.");

        Dictionary<string, JsonElement>? providerMetadata = null;
        try
        {
            providerMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new Dictionary<string, JsonElement>
                {
                    ["create"] = createTask.Root.Clone(),
                    ["result"] = finalTask.Root.Clone()
                }, JsonSerializerOptions.Web)
            };
        }
        catch
        {
            // best-effort only
        }

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = ResolveTimestamp(finalTask.Root, now),
                ModelId = request.Model,
                Body = finalTask.Root.Clone()
            }
        };
    }

    private async Task<AtlasCloudVideoTaskResult> PollVideoResultAsync(string requestId, CancellationToken cancellationToken)
    {
        var pollUrl = AtlasCloudVideoResultEndpointBase + Uri.EscapeDataString(requestId);
        using var pollReq = new HttpRequestMessage(HttpMethod.Get, pollUrl);
        using var pollResp = await _client.SendAsync(pollReq, cancellationToken);
        var pollRaw = await pollResp.Content.ReadAsStringAsync(cancellationToken);

        if (!pollResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"AtlasCloud video polling failed ({(int)pollResp.StatusCode}): {pollRaw}");

        return ParseVideoTaskResult(pollRaw);
    }

    private static AtlasCloudVideoTaskResult ParseVideoTaskResult(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : null;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        return new AtlasCloudVideoTaskResult(id, status, root);
    }

    private async Task<List<VideoResponseFile>> ExtractVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("outputs", out var outputsEl) || outputsEl.ValueKind != JsonValueKind.Array)
            return [];

        List<VideoResponseFile> videos = [];
        foreach (var output in outputsEl.EnumerateArray())
        {
            if (output.ValueKind != JsonValueKind.String)
                continue;

            var value = output.GetString();
            if (string.IsNullOrWhiteSpace(value))
                continue;

            videos.Add(await NormalizeVideoOutputAsync(value, cancellationToken));
        }

        return videos;
    }

    private async Task<VideoResponseFile> NormalizeVideoOutputAsync(string output, CancellationToken cancellationToken)
    {
        var value = output.Trim();

        if (TryParseDataUrl(value, out var dataUrlMediaType, out var dataUrlData))
        {
            return new VideoResponseFile
            {
                MediaType = string.IsNullOrWhiteSpace(dataUrlMediaType) ? DefaultVideoMimeType : dataUrlMediaType,
                Data = dataUrlData
            };
        }

        if (IsHttpUrl(value))
        {
            using var resp = await _client.GetAsync(value, cancellationToken);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"AtlasCloud video download failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

            var mediaType = resp.Content.Headers.ContentType?.MediaType
                ?? GuessVideoMediaType(value)
                ?? DefaultVideoMimeType;

            return new VideoResponseFile
            {
                MediaType = mediaType,
                Data = Convert.ToBase64String(bytes)
            };
        }

        return new VideoResponseFile
        {
            MediaType = DefaultVideoMimeType,
            Data = value
        };
    }

    private static string NormalizeVideoImageInput(VideoFile image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (string.IsNullOrWhiteSpace(image.Data))
            throw new ArgumentException("Image data is required.", nameof(image));

        var value = image.Data.Trim();

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || IsHttpUrl(value))
            return value;

        var mediaType = string.IsNullOrWhiteSpace(image.MediaType)
            ? MediaTypeNames.Image.Jpeg
            : image.MediaType;

        return value.ToDataUrl(mediaType);
    }

    private static void MergeProviderOptions(Dictionary<string, object?> payload, VideoRequest request, string providerId)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue(providerId, out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
        {
            if (payload.ContainsKey(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static string? GuessVideoMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
            return "video/webm";

        if (url.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            return "video/mp4";

        if (url.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            return "video/quicktime";

        return null;
    }

    private static bool TryParseDataUrl(string value, out string mediaType, out string data)
    {
        mediaType = DefaultVideoMimeType;
        data = string.Empty;

        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return false;

        var commaIndex = value.IndexOf(',');
        if (commaIndex <= 5 || commaIndex >= value.Length - 1)
            return false;

        var header = value[5..commaIndex];
        var payload = value[(commaIndex + 1)..];

        var segments = header.Split(';', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length > 0)
            mediaType = segments[0];

        data = payload;
        return true;
    }
}
