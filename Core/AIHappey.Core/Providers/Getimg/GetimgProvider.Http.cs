using System.Globalization;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Getimg;

public partial class GetimgProvider
{
    private static readonly JsonSerializerOptions GetimgJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string NormalizeGetimgModel(string model)
    {
        var value = model.Trim();
        var prefix = GetIdentifier() + "/";
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? value[prefix.Length..] : value;
    }

    private static Dictionary<string, object?> CreateGetimgPayload(JsonElement providerOptions)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions.ValueKind != JsonValueKind.Object)
            return payload;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();

        return payload;
    }

    private async Task<JsonElement> SendGetimgAsync(
        HttpMethod method,
        string path,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = CreateGetimgRequest(method, path);
        if (payload is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(payload, GetimgJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json);
        }

        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"getimg request '{path}' failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadGetimgOutputAsync(
        string url,
        string? mediaType,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        var uri = new Uri(url, UriKind.Absolute);
        // Only attach the API key when downloading from getimg's own API host.
        using var request = uri.Host.Equals(_client.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase)
            ? CreateGetimgRequest(HttpMethod.Get, uri.ToString())
            : new HttpRequestMessage(HttpMethod.Get, uri);

        using var response = await _client.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"getimg output download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        return (bytes, mediaType ?? response.Content.Headers.ContentType?.MediaType ?? fallbackMediaType);
    }

    private static IEnumerable<JsonElement> GetGetimgOutputs(JsonElement root)
        => root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
            : [];

    private static decimal? GetGetimgCost(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object
            || !usage.TryGetProperty("total_cost", out var cost))
            return null;

        if (cost.ValueKind == JsonValueKind.Number && cost.TryGetDecimal(out var number))
            return number;

        return cost.ValueKind == JsonValueKind.String
            && decimal.TryParse(cost.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)
                ? parsed
                : null;
    }

    private static string? GetGetimgString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

    private static string GetGetimgError(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? "Generation failed.";
            if (GetGetimgString(error, "message") is { Length: > 0 } message)
                return message;
        }

        return "Generation failed.";
    }

    private static string ToGetimgInputUrl(ImageFile file)
        => file is ImageFileUrl url ? url.Url : (file.Data ?? string.Empty).ToDataUrl(file.MediaType ?? MediaTypeNames.Image.Png);

    private static string ToGetimgInputUrl(VideoFile file)
        => file is VideoFileUrl url ? url.Url : (file.Data ?? string.Empty).ToDataUrl(file.MediaType ?? MediaTypeNames.Image.Png);
}
