using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    private const string AIMLVideoOperationTokenPrefix = "aimlv1_";

    public async Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var payload = BuildAIMLVideoPayload(request);
        if (!payload.ContainsKey("prompt") && !payload.ContainsKey("multi_prompt"))
            throw new ArgumentException("Prompt or providerOptions.aiml.multi_prompt is required.", nameof(request));

        ApplyAuthHeader();
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "v2/video/generations")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonOpts),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var createResponse = await _client.SendAsync(
            createRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var root = await ReadAIMLVideoJsonAsync(createResponse, "video generation", cancellationToken);
        var generationId = ReadAIMLVideoString(root, "id")
            ?? ReadAIMLVideoString(root, "generation_id");
        if (string.IsNullOrWhiteSpace(generationId))
            throw new InvalidOperationException("AIML video generation did not return an id or generation_id.");

        return new VideoOperationStartResult
        {
            Operation = EncodeAIMLVideoOperation(generationId, request.Model),
            Warnings = GetAIMLVideoWarnings(request),
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Response = new HeaderResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = createResponse.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier())
            }
        };
    }

    public async Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
    {
        var operationData = DecodeAIMLVideoOperation(operation);
        ApplyAuthHeader();
        using var statusRequest = new HttpRequestMessage(
            HttpMethod.Get,
            $"v2/video/generations?generation_id={Uri.EscapeDataString(operationData.Id)}");
        using var statusResponse = await _client.SendAsync(
            statusRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var root = await ReadAIMLVideoJsonAsync(statusResponse, "video status", cancellationToken);
        var status = ReadAIMLVideoString(root, "status")?.Trim().ToLowerInvariant();
        var metadata = GetIdentifier().CreatePrimitiveProviderMetadata(root);
        var response = new HeaderResponseData
        {
            Timestamp = DateTime.UtcNow,
            Headers = statusResponse.GetHeaders(),
            // Poll responses frequently omit model, so the submitted model in the token is authoritative.
            ModelId = operationData.Model.ToModelId(GetIdentifier())
        };

        if (status is "error" or "failed" or "cancelled" or "canceled")
        {
            return new VideoOperationErrorResult
            {
                Error = ReadAIMLVideoString(root, "error", "message")
                    ?? ReadAIMLVideoString(root, "error", "name")
                    ?? ReadAIMLVideoString(root, "message")
                    ?? $"AIML video generation '{operationData.Id}' failed with status '{status}'.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        if (status is not "completed" and not "succeeded" and not "success")
        {
            return new VideoOperationPendingResult
            {
                Warnings = [],
                ProviderMetadata = metadata,
                Response = response
            };
        }

        var urls = ExtractAIMLVideoUrls(root);
        if (urls.Count == 0)
        {
            return new VideoOperationErrorResult
            {
                Error = $"AIML video generation '{operationData.Id}' completed without a video URL.",
                ProviderMetadata = metadata,
                Response = response
            };
        }

        List<VideoOperationVideoData> videos = [];
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            var media = await DownloadAIMLVideoAsync(url, cancellationToken);
            videos.Add(new VideoOperationVideoData
            {
                Type = "base64",
                Data = Convert.ToBase64String(media.Bytes),
                MediaType = media.MediaType
            });
        }

        return new VideoOperationCompletedResult
        {
            Videos = videos,
            Warnings = [],
            ProviderMetadata = metadata,
            Response = response
        };
    }

    private Dictionary<string, object?> BuildAIMLVideoPayload(VideoRequest request)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var options)
            && options.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in options.EnumerateObject())
                payload[option.Name] = option.Value.Clone();
        }

        payload["model"] = request.Model.Trim();
        if (!string.IsNullOrWhiteSpace(request.Prompt)) payload["prompt"] = request.Prompt;
        SetAIMLVideoValue(payload, "resolution", request.Resolution);
        SetAIMLVideoValue(payload, "aspect_ratio", request.AspectRatio);
        SetAIMLVideoValue(payload, "seed", request.Seed);
        SetAIMLVideoValue(payload, "duration", request.Duration);
        SetAIMLVideoValue(payload, "generate_audio", request.GenerateAudio);

        AddAIMLVideoImages(payload, request);
        AddAIMLVideoReferences(payload, request);
        return payload;
    }

    private static void AddAIMLVideoImages(Dictionary<string, object?> payload, VideoRequest request)
    {
        if (request.Image is not null)
            payload["image_url"] = ToAIMLVideoMediaUrl(request.Image);

        foreach (var frame in request.FrameImages ?? [])
        {
            if (frame?.Image is null) continue;
            if (IsAIMLFirstFrame(frame.FrameType))
                payload["image_url"] = ToAIMLVideoMediaUrl(frame.Image);
            else if (IsAIMLLastFrame(frame.FrameType))
                payload["last_image_url"] = ToAIMLVideoMediaUrl(frame.Image);
        }
    }

    private static void AddAIMLVideoReferences(Dictionary<string, object?> payload, VideoRequest request)
    {
        var images = new List<string>();
        var videos = new List<string>();
        var audios = new List<string>();
        foreach (var reference in request.InputReferences ?? [])
        {
            if (reference is null) continue;
            var url = ToAIMLVideoMediaUrl(reference);
            if (reference.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true)
                videos.Add(url);
            else if (reference.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true)
                audios.Add(url);
            else
                images.Add(url);
        }

        if (images.Count > 0) payload["reference_image_urls"] = images;
        if (audios.Count > 0) payload["audio_urls"] = audios;
        if (videos.Count == 0) return;

        if (string.Equals(request.Model, "runway/gen4_aleph", StringComparison.OrdinalIgnoreCase))
            payload["video_url"] = videos[0];
        else
            payload["video_urls"] = videos;
    }

    private static IEnumerable<object> GetAIMLVideoWarnings(VideoRequest request)
    {
        List<object> warnings = [];
        if (request.Fps is not null) warnings.Add(new { type = "unsupported", feature = "fps" });
        if (request.N is not null) warnings.Add(new { type = "unsupported", feature = "n" });
        if (string.Equals(request.Model, "runway/gen4_aleph", StringComparison.OrdinalIgnoreCase)
            && request.InputReferences?.Count(reference => reference?.MediaType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) == true) > 1)
            warnings.Add(new { type = "unsupported", feature = "multiple video references" });
        return warnings;
    }

    private static void SetAIMLVideoValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text)))
            payload[name] = value;
    }

    private static string ToAIMLVideoMediaUrl(VideoFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (string.IsNullOrWhiteSpace(file.Data))
            throw new ArgumentException("Video media data is required.", nameof(file));
        if (file.Data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return file.Data;
        return $"data:{(string.IsNullOrWhiteSpace(file.MediaType) ? "application/octet-stream" : file.MediaType)};base64,{file.Data}";
    }

    private static bool IsAIMLFirstFrame(string? frameType)
        => string.Equals(frameType, "first_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "firstFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "first", StringComparison.OrdinalIgnoreCase);

    private static bool IsAIMLLastFrame(string? frameType)
        => string.Equals(frameType, "last_frame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "lastFrame", StringComparison.OrdinalIgnoreCase)
            || string.Equals(frameType, "last", StringComparison.OrdinalIgnoreCase);

    private static async Task<JsonElement> ReadAIMLVideoJsonAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIML {operation} failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"AIML {operation} returned invalid JSON.", exception);
        }
    }

    private static string? ReadAIMLVideoString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string? ReadAIMLVideoString(JsonElement element, string name, string nestedName)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var nested)
            ? ReadAIMLVideoString(nested, nestedName)
            : null;

    private static List<string> ExtractAIMLVideoUrls(JsonElement root)
    {
        var urls = new List<string>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("video", out var video))
            return urls;
        AddAIMLVideoUrls(video, urls);
        return urls;
    }

    private static void AddAIMLVideoUrls(JsonElement value, List<string> urls)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var directUrl = value.GetString();
                if (!string.IsNullOrWhiteSpace(directUrl)) urls.Add(directUrl);
                break;
            case JsonValueKind.Object:
                var url = ReadAIMLVideoString(value, "url");
                if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) AddAIMLVideoUrls(item, urls);
                break;
        }
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadAIMLVideoAsync(
        string url,
        CancellationToken cancellationToken)
    {
        if (url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return DecodeAIMLVideoDataUrl(url);

        using var downloadResponse = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await downloadResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!downloadResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIML video download failed ({(int)downloadResponse.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        return (bytes, downloadResponse.Content.Headers.ContentType?.MediaType ?? GuessAIMLVideoMediaType(url));
    }

    private static (byte[] Bytes, string MediaType) DecodeAIMLVideoDataUrl(string dataUrl)
    {
        var comma = dataUrl.IndexOf(',');
        if (comma < 0) throw new InvalidOperationException("AIML returned an invalid video data URL.");
        var header = dataUrl[5..comma];
        var mediaType = header.Split(';', 2)[0];
        if (string.IsNullOrWhiteSpace(mediaType)) mediaType = "video/mp4";
        var data = dataUrl[(comma + 1)..];
        try
        {
            return (header.Contains(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(data)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data)), mediaType);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("AIML returned an invalid base64 video data URL.", exception);
        }
    }

    private static string GuessAIMLVideoMediaType(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".webm" => "video/webm",
            ".mov" => "video/quicktime",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            _ => "video/mp4"
        };
    }

    private static string EncodeAIMLVideoOperation(string id, string model)
    {
        var json = JsonSerializer.Serialize(new AIMLVideoOperation(id, model));
        return AIMLVideoOperationTokenPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static AIMLVideoOperation DecodeAIMLVideoOperation(string operation)
    {
        if (string.IsNullOrWhiteSpace(operation)
            || !operation.StartsWith(AIMLVideoOperationTokenPrefix, StringComparison.Ordinal))
            throw new ArgumentException("A model-aware AIML video operation token is required.", nameof(operation));
        try
        {
            var encoded = operation[AIMLVideoOperationTokenPrefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
            var value = JsonSerializer.Deserialize<AIMLVideoOperation>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
            if (value is null || string.IsNullOrWhiteSpace(value.Id) || string.IsNullOrWhiteSpace(value.Model))
                throw new JsonException("Missing operation values.");
            return value;
        }
        catch (Exception exception) when (exception is FormatException or JsonException)
        {
            throw new ArgumentException("The AIML video operation token is invalid.", nameof(operation), exception);
        }
    }

    private sealed record AIMLVideoOperation(string Id, string Model);
}
