using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.MIMICXAI;

public partial class MIMICXAIProvider
{
    private async Task<MimicXJsonResult> PostJsonAsync(string endpoint, object payload, string operation,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, AgentJson), Encoding.UTF8, "application/json")
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"MIMICXAI {operation} failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        try
        {
            using var document = JsonDocument.Parse(raw);
            ThrowAgentPayloadError(document.RootElement);
            return new MimicXJsonResult(document.RootElement.Clone(), response.GetHeaders());
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"MIMICXAI {operation} returned invalid JSON: {raw}", exception);
        }
    }

    private static string RequireBase64(JsonElement root, string type, string property)
    {
        var actualType = GetString(root, "type");
        if (!string.Equals(actualType, type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"MIMICXAI returned '{actualType ?? "unknown"}' while '{type}' was required.");
        var value = GetString(root, property);
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"MIMICXAI {type} response did not contain '{property}'.");
        return StripDataUrl(value);
    }

    private static string StripDataUrl(string value)
    {
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? value[(marker + 8)..] : value;
    }

    private static (byte[] Bytes, string MediaType) DecodeAudio(object audio, string? mediaType)
    {
        var value = audio switch
        {
            byte[] bytes => Convert.ToBase64String(bytes),
            string text => text,
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => throw new ArgumentException("Audio must be byte[], base64 text, or a data URL.", nameof(audio))
        };
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var semicolon = value.IndexOf(';');
            if (semicolon > 5) mediaType = value[5..semicolon];
        }
        try { return (Convert.FromBase64String(StripDataUrl(value)), mediaType ?? "audio/mpeg"); }
        catch (FormatException exception) { throw new ArgumentException("Audio is not valid base64.", nameof(audio), exception); }
    }

    private static string AudioFileName(string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => "audio.wav",
        "audio/webm" => "audio.webm",
        "audio/ogg" => "audio.ogg",
        "audio/mp4" or "audio/m4a" => "audio.m4a",
        _ => "audio.mp3"
    };

    private sealed record MimicXJsonResult(JsonElement Root, Dictionary<string, string> Headers);
}
