using AIHappey.Vercel.Models;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Blink;

public partial class BlinkProvider
{
    private async Task<VideoResponse> VideoRequestBlink(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var startedAt = DateTime.UtcNow;
        var warnings = BuildVideoWarnings(request);

        var blocked = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "prompt", "model", "duration", "aspect_ratio", "image_url"
        };

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = request.Model
        };

        if (request.Duration is > 0)
            payload["duration"] = $"{request.Duration.Value}s";

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        MergeRawProviderOptions(payload, request.ProviderOptions, GetIdentifier(), blocked);

        // reserve canonical mapping precedence
        payload["prompt"] = request.Prompt;
        payload["model"] = request.Model;
        if (request.Duration is > 0)
            payload["duration"] = $"{request.Duration.Value}s";
        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            payload["aspect_ratio"] = request.AspectRatio;

        var json = JsonSerializer.Serialize(payload, BlinkMediaJsonOptions);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/ai/video")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Blink API error: {(int)response.StatusCode} {response.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var videos = await ExtractVideosAsync(root, warnings, cancellationToken);

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = startedAt,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static List<object> BuildVideoWarnings(VideoRequest request)
    {
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            AddUnsupportedWarning(warnings, "resolution", "Blink video endpoint does not accept resolution directly.");

        if (request.Seed is not null)
            AddUnsupportedWarning(warnings, "seed");

        if (request.Fps is not null)
            AddUnsupportedWarning(warnings, "fps");

        if (request.N is not null)
            AddUnsupportedWarning(warnings, "n");

        if (request.Image is not null)
            AddUnsupportedWarning(warnings, "image", "Image input in request primitives is not supported for Blink video endpoint.");

        return warnings;
    }

    private async Task<List<VideoResponseFile>> ExtractVideosAsync(JsonElement root, List<object> warnings, CancellationToken cancellationToken)
    {
        var videos = new List<VideoResponseFile>();

        if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            return videos;

        if (!result.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
            return videos;

        var url = urlEl.GetString();
        if (string.IsNullOrWhiteSpace(url))
            return videos;

        try
        {
            var downloaded = await TryFetchAsBase64Async(url, cancellationToken);
            if (downloaded is null)
            {
                warnings.Add(new { type = "fetch_failed", url, details = "Unable to fetch Blink video output URL." });
                return videos;
            }

            videos.Add(new VideoResponseFile
            {
                Type = "base64",
                Data = downloaded.Value.Base64,
                MediaType = downloaded.Value.MediaType
            });
        }
        catch
        {
            warnings.Add(new { type = "fetch_failed", url, details = "Unable to fetch Blink video output URL." });
        }

        return videos;
    }
}

