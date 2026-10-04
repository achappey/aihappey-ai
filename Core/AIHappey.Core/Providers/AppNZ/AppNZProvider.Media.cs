using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    private HeaderResponseData CreateMediaResponse(string model)
        => new()
        {
            ModelId = model.StartsWith(GetIdentifier() + "/", StringComparison.Ordinal)
                ? model : GetIdentifier() + "/" + model,
            Timestamp = DateTime.UtcNow
        };

    private static Dictionary<string, object?> GetMediaOptions(Dictionary<string, JsonElement>? options)
        => Options(options?.ToDictionary(item => item.Key, item => (object?)item.Value.Clone()));

    private static string? ReadOptionString(Dictionary<string, object?> options, string key)
    {
        if (!options.TryGetValue(key, out var value))
            return null;

        return value is JsonElement { ValueKind: JsonValueKind.String } json
            ? json.GetString() : value as string;
    }

    private static string? ReadString(JsonElement root, string key)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static JsonElement GetPrediction(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("prediction", out var prediction)
            && prediction.ValueKind == JsonValueKind.Object ? prediction : root;

    private static string? GetJobId(JsonElement raw)
        => ReadString(GetPrediction(raw), "id") ?? ReadString(raw, "id") ?? ReadString(raw, "request_id");

    private static string? GetJobStatus(JsonElement raw)
        => ReadString(GetPrediction(raw), "status")?.ToLowerInvariant();

    private static string GetMediaError(JsonElement raw, string fallback)
    {
        var root = GetPrediction(raw);
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            return error.ValueKind == JsonValueKind.String ? error.GetString() ?? fallback : ReadString(error, "message") ?? fallback;
        return fallback;
    }

    private Dictionary<string, JsonElement> CreateMediaMetadata(JsonElement raw)
        => GetIdentifier().CreatePrimitiveProviderMetadata(raw);

    private static string GetVideoInput(VideoFile file, bool video)
    {
        if (file.MediaType is not null && !file.MediaType.StartsWith(video ? "video/" : "image/", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException(video ? "Source must be a video." : "Video generation references must be images.");
        if (file is VideoFileUrl url)
        {
            ValidateMediaUrl(url.Url);
            return url.Url;
        }
        if (video)
            throw new NotSupportedException("AppNZ video edits/extensions require a publicly accessible video URL; inline video upload is not implemented.");
        if (string.IsNullOrWhiteSpace(file.Data) || string.IsNullOrWhiteSpace(file.MediaType))
            throw new ArgumentException("Inline image requires data and mediaType.");
        return file.Data.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? file.Data : $"data:{file.MediaType};base64,{file.Data}";
    }

    private static string? GetMediaUrl(JsonElement raw, string kind)
    {
        // The public UI scans recursive output URLs. Do not treat input or job/status URLs as artifacts.
        var root = GetPrediction(raw);
        var direct = ReadString(root, kind + "_url") ?? ReadString(root, "url");
        if (direct is not null)
            return direct;
        if (root.ValueKind != JsonValueKind.Object)
            return null;
        if (root.TryGetProperty(kind, out var media) && ReadString(media, "url") is { } url)
            return url;
        foreach (var key in new[] { "output", "result", "data", "videos", "audio" })
            if (root.TryGetProperty(key, out var output) && FindOutputUrl(output, kind) is { } found) return found;
        return null;
    }

    private static string? FindOutputUrl(JsonElement value, string kind)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? text : null;
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (FindOutputUrl(item, kind) is { } found) return found;
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var direct = ReadString(value, kind + "_url") ?? ReadString(value, "url");
            if (direct is not null) return direct;
            foreach (var key in new[] { kind, "output", "result", "data" })
                if (value.TryGetProperty(key, out var item) && FindOutputUrl(item, kind) is { } found) return found;
        }
        return null;
    }

    private static Uri ValidateMediaUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidOperationException("AppNZ media URLs must be HTTPS URLs without credentials.");
        return uri;
    }

    private async Task<(byte[] Bytes, string MimeType)> DownloadMediaAsync(
        string url, string kind, CancellationToken cancellationToken)
    {
        var uri = ValidateMediaUrl(url);
        // Never reuse CreateClient(): artifact hosts must not receive the AppNZ bearer token.
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var response = await _downloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var mimeType = response.Content.Headers.ContentType?.MediaType;
        if (mimeType is null or "application/octet-stream")
            mimeType = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() switch
            {
                ".webm" => "video/webm",
                ".mov" => "video/quicktime",
                ".mp4" => "video/mp4",
                ".wav" => "audio/wav",
                ".ogg" => "audio/ogg",
                ".m4a" => "audio/mp4",
                ".flac" => "audio/flac",
                ".mp3" => "audio/mpeg",
                _ => throw new NotSupportedException("AppNZ artifact has no verified media content type or recognizable extension.")
            };
        if (!mimeType.StartsWith(kind + "/", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"AppNZ {kind} artifact has unexpected content type '{mimeType}'.");
        return (await response.Content.ReadAsByteArrayAsync(cancellationToken), mimeType);
    }
}
