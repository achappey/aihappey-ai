using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Bytez;

public partial class BytezProvider
{
    private static readonly JsonSerializerOptions BytezSpeechJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Voice))
            warnings.Add(new { type = "unsupported", feature = "voice" });
        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, request.Model)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, BytezSpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Bytez speech request failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind != JsonValueKind.Null)
            throw new InvalidOperationException($"Bytez speech request failed: {errorEl.GetRawText()}");

        var outputUrl = root.TryGetProperty("output", out var outputEl) ? outputEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(outputUrl))
            throw new InvalidOperationException("Bytez speech response missing 'output' URL.");

        using var fileResponse = await _client.GetAsync(outputUrl, cancellationToken);
        var fileBytes = await fileResponse.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!fileResponse.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(fileBytes);
            throw new InvalidOperationException($"Bytez speech audio download failed ({(int)fileResponse.StatusCode}): {err}");
        }

        var mimeType = fileResponse.Content.Headers.ContentType?.MediaType
            ?? GuessAudioMediaType(outputUrl)
            ?? "audio/mpeg";

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(fileBytes),
                MimeType = mimeType,
                Format = MapMimeToAudioFormat(mimeType)
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string MapMimeToAudioFormat(string mimeType)
    {
        var mt = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
        return mt switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "audio/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/flac" => "flac",
            "audio/aac" => "aac",
            "audio/mp4" => "m4a",
            "audio/webm" => "webm",
            _ => "mp3"
        };
    }

    private static string? GuessAudioMediaType(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        var u = url.Trim().ToLowerInvariant();
        if (u.Contains(".mp3")) return "audio/mpeg";
        if (u.Contains(".wav")) return "audio/wav";
        if (u.Contains(".ogg")) return "audio/ogg";
        if (u.Contains(".opus")) return "audio/opus";
        if (u.Contains(".flac")) return "audio/flac";
        if (u.Contains(".aac")) return "audio/aac";
        if (u.Contains(".m4a") || u.Contains(".mp4")) return "audio/mp4";
        if (u.Contains(".webm")) return "audio/webm";
        return null;
    }
}
