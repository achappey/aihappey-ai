using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.PreAPI;

public partial class PreAPIProvider
{
    private static readonly JsonSerializerOptions PreApiJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<JsonDocument> GenerateAsync(string model, Dictionary<string, object?> input, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/generate")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, PreApiJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"PreAPI generate failed ({(int)resp.StatusCode}): {raw}");

        var doc = JsonDocument.Parse(raw);
        if (!TryGetBoolean(doc.RootElement, "success", defaultValue: true))
            throw new InvalidOperationException($"PreAPI generate returned an unsuccessful response: {raw}");

        return doc;
    }

    private static Dictionary<string, object?> BuildImageInput(ImageRequest request, List<object> warnings)
    {
        var input = CreateInputFromMetadata(request.GetProviderMetadata<JsonElement>(GetIdentifierStatic()));
        input["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            input["aspect_ratio"] = request.AspectRatio;

        if (request.Seed is not null)
            input["seed"] = request.Seed;

        if (TryParseSize(request.Size, out var width, out var height))
        {
            input["width"] = width;
            input["height"] = height;
        }
        else if (!string.IsNullOrWhiteSpace(request.Size))
        {
            warnings.Add(new { type = "unsupported", feature = "size", details = $"Could not parse size '{request.Size}' into width/height." });
        }

        if (request.Files?.FirstOrDefault() is { } firstFile)
            input["image"] = firstFile.Data.ToDataUrl(firstFile.MediaType);

        return input;
    }

    private static Dictionary<string, object?> BuildSpeechInput(SpeechRequest request)
    {
        var input = CreateInputFromMetadata(request.GetProviderMetadata<JsonElement>(GetIdentifierStatic()));
        input["prompt"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            input["voice"] = request.Voice;

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            input["output_format"] = request.OutputFormat;

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            input["instructions"] = request.Instructions;

        if (request.Speed is not null)
            input["speed"] = request.Speed;

        if (!string.IsNullOrWhiteSpace(request.Language))
            input["language"] = request.Language;

        return input;
    }

    private static Dictionary<string, object?> BuildVideoInput(VideoRequest request)
    {
        var input = CreateInputFromMetadata(request.GetProviderMetadata<JsonElement>(GetIdentifierStatic()));
        input["prompt"] = request.Prompt;

        if (!string.IsNullOrWhiteSpace(request.Resolution))
            input["resolution"] = request.Resolution;

        if (!string.IsNullOrWhiteSpace(request.AspectRatio))
            input["aspect_ratio"] = request.AspectRatio;

        if (request.Seed is not null)
            input["seed"] = request.Seed;

        if (request.Duration is not null)
            input["duration"] = request.Duration;

        if (request.Image is not null)
            input["image"] = request.Image.Data.ToDataUrl(request.Image.MediaType);

        return input;
    }

    private static Dictionary<string, object?> CreateInputFromMetadata(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return [];

        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in metadata.EnumerateObject())
            result[property.Name] = JsonSerializer.Deserialize<object?>(property.Value.GetRawText(), PreApiJsonOptions);

        return result;
    }

    private static JsonElement GetResponseData(JsonElement root)
    {
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data;

        throw new InvalidOperationException("PreAPI response contained no data object.");
    }

    private static JsonElement GetOutput(JsonElement data)
    {
        if (data.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object)
            return output;

        throw new InvalidOperationException("PreAPI response contained no output object.");
    }

    private static IEnumerable<(string? Url, string? ContentType)> GetImageOutputs(JsonElement output)
    {
        if (!output.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
            return Enumerable.Empty<(string?, string?)>();

        return images.EnumerateArray().Select(x =>
        {
            var url = x.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String ? urlEl.GetString() : null;
            var contentType = x.TryGetProperty("content_type", out var ctEl) && ctEl.ValueKind == JsonValueKind.String ? ctEl.GetString() : null;
            return (url, contentType);
        }).ToArray();
    }

    private async Task<(string Base64, string MediaType)> DownloadMediaAsync(string url, string? fallbackMediaType, CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync(url, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"PreAPI media download failed ({(int)resp.StatusCode}): {err}");
        }

        var mediaType = resp.Content.Headers.ContentType?.MediaType
            ?? fallbackMediaType
            ?? GuessMediaTypeFromUrl(url)
            ?? MediaTypeNames.Application.Octet;

        return (Convert.ToBase64String(bytes), mediaType);
    }

    private static Dictionary<string, JsonElement> CreateProviderMetadata(JsonElement root)
        => new()
        {
            [GetIdentifierStatic()] = root.Clone()
        };

    private static string? TryGetString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static string? TryGetNestedString(JsonElement element, string objectProperty, string valueProperty)
    {
        if (!element.TryGetProperty(objectProperty, out var nested) || nested.ValueKind != JsonValueKind.Object)
            return null;

        return TryGetString(nested, valueProperty);
    }

    private static bool TryGetBoolean(JsonElement element, string propertyName, bool defaultValue = false)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : defaultValue;

    private static bool TryParseSize(string? size, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (string.IsNullOrWhiteSpace(size))
            return false;

        var parts = size.Split('x', 'X', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
            return false;

        return int.TryParse(parts[0], out width) && int.TryParse(parts[1], out height);
    }

    private static string GuessAudioFormat(string mediaType, string? url)
    {
        if (mediaType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
            return "mp3";
        if (mediaType.Equals("audio/wav", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("audio/x-wav", StringComparison.OrdinalIgnoreCase))
            return "wav";
        if (mediaType.Equals("audio/flac", StringComparison.OrdinalIgnoreCase))
            return "flac";
        if (mediaType.Equals("audio/aac", StringComparison.OrdinalIgnoreCase))
            return "aac";
        if (mediaType.Equals("audio/ogg", StringComparison.OrdinalIgnoreCase) || mediaType.Equals("audio/opus", StringComparison.OrdinalIgnoreCase))
            return "opus";

        return Path.GetExtension(url ?? string.Empty).TrimStart('.').ToLowerInvariant() switch
        {
            "mp3" => "mp3",
            "wav" => "wav",
            "flac" => "flac",
            "aac" => "aac",
            "opus" or "ogg" => "opus",
            _ => "mp3"
        };
    }

    private static string? GuessMediaTypeFromUrl(string? url)
    {
        return Path.GetExtension(url ?? string.Empty).TrimStart('.').ToLowerInvariant() switch
        {
            "png" => MediaTypeNames.Image.Png,
            "jpg" or "jpeg" => MediaTypeNames.Image.Jpeg,
            "webp" => "image/webp",
            "gif" => MediaTypeNames.Image.Gif,
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "aac" => "audio/aac",
            "ogg" => "audio/ogg",
            "opus" => "audio/opus",
            "mp4" => "video/mp4",
            "webm" => "video/webm",
            "mov" => "video/quicktime",
            _ => null
        };
    }

    private static string GetIdentifierStatic() => nameof(PreAPI).ToLowerInvariant();
}
