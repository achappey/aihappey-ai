using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Eliza;

public partial class ElizaProvider
{
    private const string ElizaDefaultVideoModel = "fal-ai/veo3";

    private static readonly JsonSerializerOptions ElizaVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? ElizaDefaultVideoModel
            : request.Model.Trim();

        AddElizaVideoWarnings(request, warnings);

        var payload = new Dictionary<string, object?>
        {
            ["prompt"] = request.Prompt,
            ["model"] = model
        };

        AddElizaVideoMetadataPassthrough(payload, metadata);
        payload["prompt"] = request.Prompt;
        payload["model"] = model;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/generate-video")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, ElizaVideoJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Eliza video generation failed ({(int)response.StatusCode})."
                : $"Eliza video generation failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement.Clone();
        var videos = await ExtractElizaVideosAsync(root, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException("Eliza video generation returned no videos.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = root.TryGetString("model") ?? model,
                Body = root
            }
        };
    }

    private static void AddElizaVideoWarnings(VideoRequest request, List<object> warnings)
    {
        if (!string.IsNullOrWhiteSpace(request.Resolution))
            warnings.Add(new { type = "unsupported", feature = "resolution" });

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            warnings.Add(new { type = "unsupported", feature = "aspectRatio" });

        if (request.Duration is not null)
            warnings.Add(new { type = "unsupported", feature = "duration" });

        if (request.Fps is not null)
            warnings.Add(new { type = "unsupported", feature = "fps" });

        if (request.Seed is not null)
            warnings.Add(new { type = "unsupported", feature = "seed" });

        if (request.N is not null && request.N > 1)
            warnings.Add(new { type = "unsupported", feature = "n" });

        if (request.Image is not null)
            warnings.Add(new { type = "unsupported", feature = "image" });
    }

    private static void AddElizaVideoMetadataPassthrough(Dictionary<string, object?> payload, JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, "prompt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private async Task<List<VideoResponseFile>> ExtractElizaVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        if (root.TryGetProperty("video", out var videoElement))
        {
            var normalized = await NormalizeElizaVideoElementAsync(videoElement, cancellationToken);
            if (normalized is not null)
                videos.Add(normalized);
        }

        if (root.TryGetProperty("videos", out var videosElement) && videosElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in videosElement.EnumerateArray())
            {
                var normalized = await NormalizeElizaVideoElementAsync(item, cancellationToken);
                if (normalized is not null)
                    videos.Add(normalized);
            }
        }

        return videos;
    }

    private async Task<VideoResponseFile?> NormalizeElizaVideoElementAsync(JsonElement item, CancellationToken cancellationToken)
    {
        if (item.ValueKind == JsonValueKind.String)
            return await NormalizeElizaVideoValueAsync(item.GetString(), "video/mp4", cancellationToken);

        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var mediaType = GuessElizaVideoMediaType(item) ?? "video/mp4";
        var base64 = item.TryGetString("b64_json")
            ?? item.TryGetString("base64")
            ?? item.TryGetString("data");

        if (!string.IsNullOrWhiteSpace(base64))
        {
            return new VideoResponseFile
            {
                Data = ExtractElizaVideoBase64Payload(base64),
                MediaType = mediaType
            };
        }

        var url = item.TryGetString("url")
            ?? item.TryGetString("video_url")
            ?? item.TryGetString("videoUrl");

        return await NormalizeElizaVideoValueAsync(url, mediaType, cancellationToken);
    }

    private async Task<VideoResponseFile?> NormalizeElizaVideoValueAsync(
        string? value,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return new VideoResponseFile
            {
                Data = ExtractElizaVideoBase64Payload(trimmed),
                MediaType = TryReadElizaVideoDataUrlMediaType(trimmed) ?? fallbackMediaType
            };
        }

        if (!LooksLikeHttpUrl(trimmed))
        {
            return new VideoResponseFile
            {
                Data = trimmed,
                MediaType = fallbackMediaType
            };
        }

        using var response = await _client.GetAsync(trimmed, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Eliza video download failed ({(int)response.StatusCode}): {error}");
        }

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = response.Content.Headers.ContentType?.MediaType
                ?? GuessElizaVideoMediaTypeFromUrl(trimmed)
                ?? fallbackMediaType
        };
    }

    private static string ExtractElizaVideoBase64Payload(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var commaIndex = trimmed.IndexOf(',');
        return commaIndex >= 0 && commaIndex < trimmed.Length - 1
            ? trimmed[(commaIndex + 1)..]
            : string.Empty;
    }

    private static string? TryReadElizaVideoDataUrlMediaType(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var semicolonIndex = value.IndexOf(';');
        if (semicolonIndex <= 5)
            return null;

        return value[5..semicolonIndex];
    }

    private static string? GuessElizaVideoMediaType(JsonElement item)
    {
        var value = item.TryGetString("mimeType")
            ?? item.TryGetString("mime_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("format");

        return NormalizeElizaVideoMediaType(value);
    }

    private static string? GuessElizaVideoMediaTypeFromUrl(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : url;

        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mp4" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".avi" => "video/x-msvideo",
            ".m4v" => "video/x-m4v",
            _ => null
        };
    }

    private static string? NormalizeElizaVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().TrimStart('.').ToLowerInvariant() switch
        {
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            "avi" => "video/x-msvideo",
            "m4v" => "video/x-m4v",
            var mime when mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => mime,
            _ => null
        };
    }

    private static bool LooksLikeHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
