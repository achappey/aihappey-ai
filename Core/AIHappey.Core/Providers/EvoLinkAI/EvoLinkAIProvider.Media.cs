using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.EvoLinkAI;

public partial class EvoLinkAIProvider
{
    private const string EvoLinkAIFileUploadEndpoint = "https://files-api.evolink.ai/api/v1/files/upload/base64";

    private async Task<string> ResolveEvoLinkAIInputUrlAsync(
        string data,
        string? mediaType,
        string? fileName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException("Media data is required.", nameof(data));

        if (Uri.TryCreate(data, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return data;

        if (string.IsNullOrWhiteSpace(mediaType) || !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("EvoLinkAI file upload currently accepts image media types only.", nameof(mediaType));

        var dataUrl = data.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? data
            : data.ToDataUrl(mediaType);

        var payload = new Dictionary<string, object?>
        {
            ["base64_data"] = dataUrl,
            ["file_name"] = string.IsNullOrWhiteSpace(fileName) ? null : fileName
        };

        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, EvoLinkAIFileUploadEndpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, EvoLinkAISpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EvoLinkAI file upload failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var fileUrl = root.TryGetString("file_url")
            ?? root.TryGetString("url");

        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
            fileUrl ??= dataElement.TryGetString("file_url")
                ?? dataElement.TryGetString("fileUrl")
                ?? dataElement.TryGetString("download_url")
                ?? dataElement.TryGetString("downloadUrl")
                ?? dataElement.TryGetString("url");

        return !string.IsNullOrWhiteSpace(fileUrl)
            ? fileUrl
            : throw new InvalidOperationException("EvoLinkAI file upload returned no accessible file URL.");
    }

    private JsonElement? GetEvoLinkAIProviderOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException($"providerOptions.{GetIdentifier()} must be a JSON object.", nameof(providerOptions));

        return options.Clone();
    }

    private static Dictionary<string, object?> CreateEvoLinkAIPassthroughPayload(JsonElement? providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions is not { ValueKind: JsonValueKind.Object } options)
            return payload;

        foreach (var property in options.EnumerateObject())
        {
            if (!IsEvoLinkAIPollControlOption(property.Name))
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static Dictionary<string, JsonElement> CreateEvoLinkAIMetadata(
        string endpoint,
        object payload,
        JsonElement create,
        JsonElement? retrieve = null,
        string? taskId = null,
        string? status = null,
        IDictionary<string, string>? createHeaders = null,
        IDictionary<string, string>? retrieveHeaders = null)
        => GetEvoLinkAIProviderMetadata(new
        {
            endpoint,
            taskEndpoint = "v1/tasks/{task_id}",
            request = payload,
            create,
            retrieve,
            taskId,
            status,
            createHeaders,
            retrieveHeaders
        });

    private static Dictionary<string, JsonElement> GetEvoLinkAIProviderMetadata(object value)
        => new()
        {
            ["evolinkai"] = JsonSerializer.SerializeToElement(value, EvoLinkAISpeechJsonOptions)
        };

    private async Task<(byte[] Bytes, string MediaType)> DownloadEvoLinkAIMediaAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EvoLinkAI media download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static IReadOnlyList<string> GetEvoLinkAIResultUrls(JsonElement root, string mediaKind)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        CollectEvoLinkAIResultUrls(root, mediaKind, results, seen);
        return results;
    }

    private static void CollectEvoLinkAIResultUrls(
        JsonElement element,
        string mediaKind,
        List<string> results,
        HashSet<string> seen)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CollectEvoLinkAIResultUrls(item, mediaKind, results, seen);
            return;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return;

        var preferredNames = mediaKind switch
        {
            "image" => new[] { "image_url", "imageUrl", "url", "download_url", "downloadUrl" },
            "video" => new[] { "video_url", "videoUrl", "url", "download_url", "downloadUrl" },
            _ => new[] { "audio_url", "audioUrl", "url", "download_url", "downloadUrl" }
        };

        foreach (var name in preferredNames)
        {
            var value = element.TryGetString(name);
            if (!string.IsNullOrWhiteSpace(value) && IsEvoLinkAIResultValue(value, mediaKind) && seen.Add(value))
                results.Add(value);
        }

        foreach (var property in element.EnumerateObject())
        {
            if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                CollectEvoLinkAIResultUrls(property.Value, mediaKind, results, seen);
            else if (property.Value.ValueKind == JsonValueKind.String
                     && IsEvoLinkAIResultContainer(property.Name, mediaKind))
            {
                var value = property.Value.GetString();
                if (!string.IsNullOrWhiteSpace(value) && IsEvoLinkAIResultValue(value, mediaKind) && seen.Add(value))
                    results.Add(value);
            }
        }
    }

    private static bool IsEvoLinkAIResultContainer(string name, string mediaKind)
        => name.Equals(mediaKind, StringComparison.OrdinalIgnoreCase)
           || name.Equals($"{mediaKind}s", StringComparison.OrdinalIgnoreCase)
           || name.Equals("result", StringComparison.OrdinalIgnoreCase)
           || name.Equals("results", StringComparison.OrdinalIgnoreCase)
           || name.Equals("output", StringComparison.OrdinalIgnoreCase)
           || name.Equals("outputs", StringComparison.OrdinalIgnoreCase);

    private static bool IsEvoLinkAIResultValue(string value, string mediaKind)
        => value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith($"data:{mediaKind}/", StringComparison.OrdinalIgnoreCase);

    private static long ResolveEvoLinkAICreatedUnix(JsonElement root)
    {
        if (root.TryGetProperty("created", out var created) && created.TryGetInt64(out var value))
            return value;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("created", out created) && created.TryGetInt64(out value))
            return value;
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }
}
