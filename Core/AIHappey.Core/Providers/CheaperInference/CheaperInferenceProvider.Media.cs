using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.CheaperInference;

public partial class CheaperInferenceProvider
{
    private async Task<CheaperInferenceJsonResult> SendCheaperInferenceJsonAsync(
        HttpMethod method, string endpoint, object payload, string operation, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(method, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, CheaperInferenceMediaJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return await ReadCheaperInferenceJsonAsync(response, operation, cancellationToken);
    }

    private static async Task<CheaperInferenceJsonResult> ReadCheaperInferenceJsonAsync(
        HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Cheaper Inference {operation} failed ({(int)response.StatusCode} {response.ReasonPhrase})."
                : $"Cheaper Inference {operation} failed ({(int)response.StatusCode}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            return new CheaperInferenceJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"Cheaper Inference {operation} returned invalid JSON.", exception);
        }
    }

    private Dictionary<string, object?> ReadCheaperInferenceOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (providerOptions is null || !providerOptions.TryGetValue(GetIdentifier(), out var options)
            || options.ValueKind != JsonValueKind.Object) return result;
        foreach (var property in options.EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }

    private static void SetCheaperInferenceValue(Dictionary<string, object?> payload, string name, object? value)
    {
        if (value is not null && (value is not string text || !string.IsNullOrWhiteSpace(text))) payload[name] = value;
    }

    private static string? ReadCheaperInferenceString(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String) return value.GetString();
        return null;
    }

    private static int? ReadCheaperInferenceInt(JsonElement root, params string[] names)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number)) return number;
        return null;
    }

    private static DateTime ReadCheaperInferenceTimestamp(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("created", out var created))
        {
            if (created.TryGetInt64(out var unix)) return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;
            if (created.ValueKind == JsonValueKind.String && DateTime.TryParse(created.GetString(), out var date)) return date.ToUniversalTime();
        }
        return DateTime.UtcNow;
    }

    private static bool IsCheaperInferenceHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static string RemoveCheaperInferenceDataUrlPrefix(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return value;
        var comma = value.IndexOf(',');
        if (comma < 0 || comma == value.Length - 1) throw new FormatException("Invalid media data URL.");
        return value[(comma + 1)..];
    }

    private static string? ReadCheaperInferenceDataUrlMediaType(string value)
    {
        if (!value.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;
        var semicolon = value.IndexOf(';');
        var comma = value.IndexOf(',');
        var end = semicolon >= 0 ? semicolon : comma;
        return end > 5 ? value[5..end] : null;
    }

    private sealed record CheaperInferenceJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
