using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.AIgateway;

public partial class AIgatewayProvider
{
    private static readonly JsonSerializerOptions AIgatewayJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly TimeSpan AIgatewayJobPollInterval = TimeSpan.FromSeconds(3);

    private static Dictionary<string, object?> CreateAIgatewayPayload(
        Dictionary<string, object?> fields,
        Dictionary<string, JsonElement>? providerOptions,
        params string[] reservedFields)
    {
        var payload = new Dictionary<string, object?>(fields, StringComparer.Ordinal);
        if (providerOptions is null
            || !providerOptions.TryGetValue(GetAIgatewayProviderOptionsKey(), out var options)
            || options.ValueKind != JsonValueKind.Object)
        {
            return payload;
        }

        var reserved = new HashSet<string>(reservedFields, StringComparer.Ordinal);
        foreach (var property in options.EnumerateObject())
        {
            if (!reserved.Contains(property.Name))
                payload[property.Name] = property.Value.Clone();
        }

        return payload;
    }

    private static string GetAIgatewayProviderOptionsKey() => nameof(AIgateway).ToLowerInvariant();

    private static HttpRequestMessage CreateAIgatewayJsonRequest(HttpMethod method, string endpoint, object payload)
        => new(method, endpoint)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, AIgatewayJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

    private static async Task<JsonElement> ReadAIgatewayJsonAsync(HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIgateway {operation} failed ({(int)response.StatusCode}): {raw}");

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.Clone();
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"AIgateway {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private async Task<(byte[] Bytes, string MimeType)> DownloadAIgatewayFileAsync(string url,
        string fallbackMimeType,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AIgateway media download failed ({(int)response.StatusCode}): {url}");

        return (bytes, response.Content.Headers.ContentType?.MediaType ?? fallbackMimeType);
    }

    private async Task<string> ResolveAIgatewayImageBase64Async(JsonElement image,
        CancellationToken cancellationToken)
    {
        if (image.TryGetProperty("b64_json", out var base64)
            && base64.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(base64.GetString()))
        {
            return base64.GetString()!;
        }

        if (image.TryGetProperty("url", out var url)
            && url.ValueKind == JsonValueKind.String
            && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var uri))
        {
            var (bytes, _) = await DownloadAIgatewayFileAsync(uri.ToString(), MediaTypeNames.Image.Png, cancellationToken);
            return Convert.ToBase64String(bytes);
        }

        throw new InvalidOperationException("AIgateway image response contained neither b64_json nor a downloadable URL.");
    }

    private async Task<AIgatewayJobResult> PollAIgatewayJobAsync(string jobId, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = new HttpRequestMessage(HttpMethod.Get, $"v1/jobs/{Uri.EscapeDataString(jobId)}");
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var root = await ReadAIgatewayJsonAsync(response, "job status", cancellationToken);
            var status = GetAIgatewayString(root, "status");

            if (IsAIgatewayTerminalStatus(status))
                return new AIgatewayJobResult(root, response.GetHeaders(), status);

            await Task.Delay(AIgatewayJobPollInterval, cancellationToken);
        }
    }

    private static bool IsAIgatewayTerminalStatus(string? status)
        => status is not null && (status.Equals("completed", StringComparison.OrdinalIgnoreCase)
                                  || status.Equals("failed", StringComparison.OrdinalIgnoreCase)
                                  || status.Equals("cancelled", StringComparison.OrdinalIgnoreCase));

    private static string? GetAIgatewayString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var property in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(property, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string ResolveAIgatewaySpeechMimeType(string? format)
        => format?.Trim().ToLowerInvariant() switch
        {
            "wav" or "pcm" => "audio/wav",
            "opus" => "audio/ogg",
            "aac" => "audio/aac",
            "flac" => "audio/flac",
            _ => "audio/mpeg"
        };

    private static string ResolveAIgatewaySpeechFormat(string? requestedFormat, string mimeType)
        => !string.IsNullOrWhiteSpace(requestedFormat)
            ? requestedFormat
            : mimeType.ToLowerInvariant() switch
            {
                "audio/wav" or "audio/x-wav" => "wav",
                "audio/ogg" or "audio/opus" => "opus",
                "audio/aac" => "aac",
                "audio/flac" => "flac",
                _ => "mp3"
            };

    private static (byte[] Bytes, string MediaType) DecodeAIgatewayBase64(object audio, string mediaType)
    {
        var value = audio is JsonElement element && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : audio?.ToString();

        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = value.IndexOf(',');
            if (comma <= 5 || comma == value.Length - 1)
                throw new FormatException("Invalid audio data URL.");

            var header = value[5..comma];
            var semicolon = header.IndexOf(';');
            mediaType = semicolon < 0 ? header : header[..semicolon];
            value = value[(comma + 1)..];
        }

        return (Convert.FromBase64String(value), mediaType);
    }

    private static string ResolveAIgatewayAudioFileName(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/wav" or "audio/x-wav" => "audio.wav",
            "audio/ogg" => "audio.ogg",
            "audio/opus" => "audio.opus",
            "audio/mp4" or "audio/m4a" => "audio.m4a",
            "audio/mpeg" => "audio.mp3",
            _ => "audio.bin"
        };

    private sealed record AIgatewayJobResult(JsonElement Root, Dictionary<string, string> Headers, string? Status);
}
