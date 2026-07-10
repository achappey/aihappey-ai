using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly Uri InfronVideoGenerationsUri = new("https://media.onerouter.pro/v1/videos/generations");

    private static readonly JsonSerializerOptions InfronVideoJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<VideoResponse> InfronVideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Prompt))
            throw new ArgumentException("Prompt is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var videoId = ReadInfronVideoOption(metadata, "video_id", "videoId");
        var isEdit = !string.IsNullOrWhiteSpace(videoId);
        var endpoint = InfronVideoGenerationsUri;
        var payload = BuildInfronVideoPayload(request, videoId, metadata, warnings);
        var json = JsonSerializer.Serialize(payload, InfronVideoJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Infron video request failed ({(int)response.StatusCode})."
                : $"Infron video request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var createRoot = doc.RootElement.Clone();
        var terminal = await WaitForInfronMediaTaskAsync("videos", createRoot, metadata, cancellationToken);

        if (!IsInfronMediaSuccessStatus(terminal.Status) && !HasInfronMediaOutputs(terminal.Root))
            throw new InvalidOperationException($"Infron video generation failed with status '{terminal.Status}': {GetInfronMediaError(terminal.Root)}");

        var videos = await ExtractInfronVideosAsync(terminal.Root, cancellationToken);

        if (videos.Count == 0)
            throw new InvalidOperationException("No valid videos returned from Infron video API.");

        return new VideoResponse
        {
            Videos = videos,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = ResolveInfronTimestamp(terminal.Root, now),
                ModelId = terminal.Root.TryGetString("model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    internal static Dictionary<string, object?> BuildInfronVideoPayload(
        VideoRequest request,
        string? videoId,
        JsonElement? metadata = null,
        List<object>? warnings = null)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["prompt"] = request.Prompt
        };

        var outputFormat = ReadInfronVideoOption(metadata, "output_format", "outputFormat", "response_format", "responseFormat");
        if (!string.IsNullOrWhiteSpace(outputFormat))
            payload["output_format"] = outputFormat;

        if (!string.IsNullOrWhiteSpace(videoId))
            payload["video_id"] = videoId;

        AddInfronVideoMetadataPassthrough(payload, metadata);
        AddInfronVideoWarnings(request, warnings);

        return payload;
    }

    private static void AddInfronVideoWarnings(VideoRequest request, List<object>? warnings)
    {
        if (warnings is null)
            return;

        if (request.Image is not null)
        {
            warnings.Add(new
            {
                type = "unsupported",
                feature = "image",
                details = "Infron video editing requires providerOptions.infron.video_id from a previous Infron generation; arbitrary image input is not documented for this endpoint."
            });
        }

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
    }

    private static void AddInfronVideoMetadataPassthrough(Dictionary<string, object?> payload, JsonElement? metadata)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } options)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (string.Equals(property.Name, "output_format", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "outputFormat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "response_format", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "responseFormat", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "video_id", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "videoId", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "endpoint", StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Name, "operation", StringComparison.OrdinalIgnoreCase)
                || IsInfronMediaControlOption(property.Name))
            {
                continue;
            }

            payload[property.Name] = property.Value.Clone();
        }
    }

    private async Task<List<VideoResponseFile>> ExtractInfronVideosAsync(JsonElement root, CancellationToken cancellationToken)
    {
        List<VideoResponseFile> videos = [];

        foreach (var item in EnumerateInfronMediaOutputItems(root))
        {
            var normalized = await NormalizeInfronVideoItemAsync(item, cancellationToken);
            if (normalized is not null)
                videos.Add(normalized);
        }

        if (videos.Count > 0 || !root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            return videos;

        foreach (var item in dataEl.EnumerateArray())
        {
            var normalized = await NormalizeInfronVideoItemAsync(item, cancellationToken);
            if (normalized is not null)
                videos.Add(normalized);
        }

        return videos;
    }

    private async Task<VideoResponseFile?> NormalizeInfronVideoItemAsync(JsonElement item, CancellationToken cancellationToken)
    {
        var dataValue = item.ValueKind == JsonValueKind.Object
            ? item.TryGetString("b64_json")
            ?? item.TryGetString("base64")
            ?? item.TryGetString("data")
            : null;

        if (!string.IsNullOrWhiteSpace(dataValue))
        {
            var mType = GuessInfronVideoMediaType(item) ?? "video/mp4";
            return new VideoResponseFile
            {
                Data = ExtractBase64Payload(dataValue),
                MediaType = mType
            };
        }

        var url = TryGetInfronMediaUrl(item, "video_url", "videoUrl");

        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var cType = GuessInfronVideoMediaType(item)
                ?? TryReadDataUrlMediaType(url)
                ?? "video/mp4";

            return new VideoResponseFile
            {
                Data = ExtractBase64Payload(url),
                MediaType = cType
            };
        }

        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Infron video download failed ({(int)response.StatusCode}): {error}");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType
            ?? GuessInfronVideoMediaType(item)
            ?? GuessInfronVideoMediaTypeFromUrl(url)
            ?? "video/mp4";

        return new VideoResponseFile
        {
            Data = Convert.ToBase64String(bytes),
            MediaType = mediaType
        };
    }

    private static string? ReadInfronVideoOption(JsonElement? metadata, params string[] names)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } options)
            return null;

        foreach (var name in names)
        {
            var value = options.TryGetString(name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static string ExtractBase64Payload(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var commaIndex = trimmed.IndexOf(',');
        return commaIndex >= 0 && commaIndex < trimmed.Length - 1
            ? trimmed[(commaIndex + 1)..]
            : string.Empty;
    }

    private static string? TryReadDataUrlMediaType(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        var semicolonIndex = value.IndexOf(';');
        if (semicolonIndex <= 5)
            return null;

        return value[5..semicolonIndex];
    }

    private static string? GuessInfronVideoMediaType(JsonElement item)
    {
        var value = item.TryGetString("mime_type")
            ?? item.TryGetString("mimeType")
            ?? item.TryGetString("content_type")
            ?? item.TryGetString("contentType")
            ?? item.TryGetString("format");

        return NormalizeInfronVideoMediaType(value);
    }

    private static string? GuessInfronVideoMediaTypeFromUrl(string url)
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
            _ => null
        };
    }

    private static string? NormalizeInfronVideoMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        return value.Trim().ToLowerInvariant() switch
        {
            "mp4" => "video/mp4",
            "mov" => "video/quicktime",
            "webm" => "video/webm",
            "avi" => "video/x-msvideo",
            var mime when mime.StartsWith("video/", StringComparison.OrdinalIgnoreCase) => mime,
            _ => null
        };
    }
}
