using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.HiNow;

public partial class HiNowProvider
{
    private static readonly JsonSerializerOptions HiNowJson = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private JsonObject GetHiNowOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var payload = new JsonObject();
        if (providerOptions is not null
            && providerOptions.TryGetValue(GetIdentifier(), out var options)
            && options.ValueKind == JsonValueKind.Object)
            foreach (var property in options.EnumerateObject())
                payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        return payload;
    }

    private static void MergeHiNowOptions(JsonObject payload, Dictionary<string, JsonElement>? options)
    {
        foreach (var option in options ?? [])
            payload[option.Key] = JsonNode.Parse(option.Value.GetRawText());
    }

    private static void SetHiNow(JsonObject payload, string name, object? value)
    {
        if (value is null || value is string text && string.IsNullOrWhiteSpace(text)) return;
        payload[name] = JsonSerializer.SerializeToNode(value, HiNowJson);
    }

    private async Task<HiNowJsonResult> SendHiNowJsonAsync(
        HttpMethod method, string endpoint, JsonObject payload, string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(payload.ToJsonString(HiNowJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"HiNow {operation} failed ({(int)response.StatusCode})."
                : $"HiNow {operation} failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return new HiNowJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"HiNow {operation} returned invalid JSON.", exception);
        }
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadHiNowMediaAsync(
        string value, string fallbackMediaType, CancellationToken cancellationToken)
    {
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0) throw new InvalidOperationException("HiNow returned an invalid media data URL.");
            var header = value[5..comma];
            var mediaType = header.Split(';', 2)[0];
            return (Convert.FromBase64String(value[(comma + 1)..]), string.IsNullOrWhiteSpace(mediaType) ? fallbackMediaType : mediaType);
        }
        using var response = await _client.GetAsync(value, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode || bytes.Length == 0)
            throw new InvalidOperationException($"HiNow media download failed ({(int)response.StatusCode}).");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? GuessHiNowMediaType(value, fallbackMediaType));
    }

    private static string GuessHiNowMediaType(string value, string fallback)
    {
        var path = Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.AbsolutePath : value;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png", ".webp" => "image/webp", ".jpg" or ".jpeg" => "image/jpeg",
            ".wav" => "audio/wav", ".ogg" => "audio/ogg", ".mp3" => "audio/mpeg", ".webm" => "video/webm",
            _ => fallback
        };
    }

    private static JsonElement GetHiNowData(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) ? data : root;

    private static string? GetHiNowString(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static List<string> GetHiNowUrls(JsonElement element)
    {
        var urls = new List<string>();
        if (element.ValueKind != JsonValueKind.Object) return urls;
        if (element.TryGetProperty("urls", out var array) && array.ValueKind == JsonValueKind.Array)
            urls.AddRange(array.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(x => !string.IsNullOrWhiteSpace(x)));
        var url = GetHiNowString(element, "url", "audio_url", "video_url", "image_url");
        if (!string.IsNullOrWhiteSpace(url)) urls.Add(url);
        return urls;
    }

    private sealed record HiNowJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
