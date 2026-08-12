using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.PrunaAI;

public partial class PrunaAIProvider
{
    private static readonly JsonSerializerOptions PrunaJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static string NormalizePrunaModel(string model)
    {
        var value = model.Trim();
        var separator = value.IndexOf('/');
        return separator >= 0 ? value[(separator + 1)..] : value;
    }

    private Dictionary<string, object?> CreatePrunaInput(JsonElement metadata)
    {
        var input = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind != JsonValueKind.Object)
            return input;

        foreach (var property in metadata.EnumerateObject())
            input[property.Name] = property.Value.Clone();

        return input;
    }

    private async Task<JsonElement> SendPrunaPredictionAsync(
        string model,
        Dictionary<string, object?> input,
        bool trySync,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, "v1/predictions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(new { input }, PrunaJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };
        request.Headers.TryAddWithoutValidation("Model", NormalizePrunaModel(model));
        if (trySync)
            request.Headers.TryAddWithoutValidation("Try-Sync", "true");

        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pruna prediction request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> GetPrunaPredictionAsync(string predictionId, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.GetAsync(
            $"v1/predictions/status/{Uri.EscapeDataString(predictionId)}",
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pruna prediction status request failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }

    private async Task<string> UploadPrunaFileAsync(string data, string? mediaType, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(data))
            throw new ArgumentException("Pruna input file data is required.", nameof(data));

        if (data.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || data.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return data;

        var bytes = DecodePrunaFile(data, ref mediaType);
        ApplyAuthHeader();
        using var form = new MultipartFormDataContent();
        using var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrWhiteSpace(mediaType) ? MediaTypeNames.Application.Octet : mediaType);
        form.Add(content, "content", $"input{GetPrunaFileExtension(mediaType)}");

        using var response = await _client.PostAsync("v1/files", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pruna file upload failed ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("urls", out var urls)
            && urls.ValueKind == JsonValueKind.Object
            && urls.TryGetProperty("get", out var get)
            && get.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(get.GetString()))
            return get.GetString()!;

        throw new InvalidOperationException("Pruna file upload response did not contain 'urls.get'.");
    }

    private async Task<(byte[] Bytes, string MediaType)> DownloadPrunaOutputAsync(
        string url,
        string fallbackMediaType,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.GetAsync(url, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Pruna output download failed ({(int)response.StatusCode}): {Encoding.UTF8.GetString(bytes)}");
        return (bytes, response.Content.Headers.ContentType?.MediaType ?? GuessPrunaMediaType(url) ?? fallbackMediaType);
    }

    private static byte[] DecodePrunaFile(string data, ref string? mediaType)
    {
        var value = data.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma < 0)
                throw new ArgumentException("Invalid data URL.", nameof(data));
            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            mediaType = semicolon >= 0 ? header[..semicolon] : header;
            value = value[(comma + 1)..];
        }
        return Convert.FromBase64String(value);
    }

    private static string? GetPrunaString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static string GetPrunaError(JsonElement root)
        => GetPrunaString(root, "error", "message") ?? "Prediction failed.";

    private static string? GuessPrunaMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var path = url.Split('?', '#')[0];
        if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return "image/png";
        if (path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase)) return "image/webp";
        if (path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return "image/jpeg";
        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "video/webm";
        if (path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)) return "video/quicktime";
        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)) return "video/mp4";
        return null;
    }

    private static string GetPrunaFileExtension(string? mediaType) => mediaType?.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "video/mp4" => ".mp4",
        "video/webm" => ".webm",
        _ => ".png"
    };
}
