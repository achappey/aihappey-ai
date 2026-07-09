using System.Text.Json;
using System.Net.Mime;
using System.Text;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.AIML;

public partial class AIMLProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed.HasValue)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        var payload = request.GetTtsRequestPayload(GetIdentifier());

        var json = JsonSerializer.Serialize(payload, JsonOpts);

        using var ttsReq = new HttpRequestMessage(HttpMethod.Post, "v1/tts")
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var ttsResp = await _client.SendAsync(ttsReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseBytes = await ttsResp.Content.ReadAsByteArrayAsync(cancellationToken);
        var responseContentType = ttsResp.Content.Headers.ContentType?.MediaType;

        if (!ttsResp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(responseBytes);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(err)
                ? $"AIML TTS failed ({(int)ttsResp.StatusCode})."
                : $"AIML TTS failed ({(int)ttsResp.StatusCode}): {err}");
        }

        using var responseDoc = JsonDocument.Parse(responseBytes);

        var responseBody = Encoding.UTF8.GetString(responseBytes);
        var root = responseDoc.RootElement;

        if (!TryGetAudioUrl(root, out var audioUrl, out var audioElement))
            throw new InvalidOperationException($"AIML TTS response returned no audio URL. Body: {responseBody}");

        using var audioReq = new HttpRequestMessage(HttpMethod.Get, audioUrl);
        using var audioResp = await _client.SendAsync(audioReq, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var audioBytes = await audioResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!audioResp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(audioBytes);
            throw new InvalidOperationException($"AIML audio download failed ({(int)audioResp.StatusCode}): {text}");
        }

        var audioContentType = audioResp.Content.Headers.ContentType?.MediaType;
        var (mime, format) = InferAudioType(audioElement, audioUrl, audioContentType, ReadRequestedAudioFormat(payload));

        var providerKey = GetIdentifier();

        var providerMetadata = new Dictionary<string, JsonElement>();

        decimal? cost = null;

        if (root.TryGetProperty("meta", out var metaEl)
             && metaEl.ValueKind == JsonValueKind.Object)
        {
            JsonElement? usageClone = null;
            JsonElement? metricsClone = null;

            if (metaEl.TryGetProperty("usage", out var usageEl)
                && usageEl.ValueKind == JsonValueKind.Object)
            {
                usageClone = usageEl.Clone();

                if (usageEl.TryGetProperty("usd_spent", out var usdSpentEl)
                    && usdSpentEl.ValueKind == JsonValueKind.Number
                    && usdSpentEl.TryGetDecimal(out var parsedCost))
                {
                    cost = parsedCost;
                }
            }

            if (metaEl.TryGetProperty("metrics", out var metricsEl)
                && metricsEl.ValueKind == JsonValueKind.Object)
            {
                metricsClone = metricsEl.Clone();
            }

            providerMetadata[providerKey] = JsonSerializer.SerializeToElement(new
            {
                usage = usageClone,
                metrics = metricsClone
            }, JsonSerializerOptions.Web);
        }

        if (cost is not null)
        {
            providerMetadata["gateway"] = JsonSerializer.SerializeToElement(new
            {
                cost
            }, JsonSerializerOptions.Web);
        }

        return CreateSpeechResponse(request, payload, warnings, now, audioBytes,
            mime, GetIdentifier(), format, root.Clone(), providerMetadata);
    }

    private static SpeechResponse CreateSpeechResponse(
     SpeechRequest request,
     Dictionary<string, object?> payload,
     List<object> warnings,
     DateTime timestamp,
     byte[] bytes,
     string mimeType,
     string providerId,
     string format,
     object? responseBody,
     Dictionary<string, JsonElement>? providerMetadata = null)
     => new()
     {
         Audio = new()
         {
             Base64 = Convert.ToBase64String(bytes),
             MimeType = mimeType,
             Format = format
         },
         Warnings = warnings,
         ProviderMetadata = providerMetadata,
         Response = new()
         {
             Timestamp = timestamp,
             ModelId = request.Model.ToModelId(providerId),
             Body = responseBody
         },
         Request = new SpeechRequestItem
         {
             Body = payload
         }
     };

    private static bool TryGetAudioUrl(JsonElement root, out string audioUrl, out JsonElement? audioElement)
    {
        audioUrl = string.Empty;
        audioElement = null;

        if (root.TryGetProperty("audio", out var audioEl))
        {
            audioElement = audioEl;

            if (audioEl.ValueKind == JsonValueKind.String)
                audioUrl = audioEl.GetString() ?? string.Empty;
            else if (audioEl.ValueKind == JsonValueKind.Object
                && audioEl.TryGetProperty("url", out var urlEl)
                && urlEl.ValueKind == JsonValueKind.String)
                audioUrl = urlEl.GetString() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(audioUrl)
            && root.TryGetProperty("audio_url", out var audioUrlEl)
            && audioUrlEl.ValueKind == JsonValueKind.String)
            audioUrl = audioUrlEl.GetString() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(audioUrl)
            && root.TryGetProperty("url", out var rootUrlEl)
            && rootUrlEl.ValueKind == JsonValueKind.String)
            audioUrl = rootUrlEl.GetString() ?? string.Empty;

        return !string.IsNullOrWhiteSpace(audioUrl);
    }

    private static string? ReadRequestedAudioFormat(Dictionary<string, object?> payload)
    {
        foreach (var key in new[] { "response_format", "output_format", "format", "encoding", "container" })
        {
            if (!payload.TryGetValue(key, out var value))
                continue;

            var text = value switch
            {
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
                _ => value?.ToString()
            };

            if (!string.IsNullOrWhiteSpace(text))
                return text.Trim();
        }

        return null;
    }

    private static (string MimeType, string Format) InferAudioType(JsonElement? audioFileEl, string? audioUrl, string? httpContentType, string? requestedFormat)
    {
        // Prefer content_type from payload if present.
        if (audioFileEl is { ValueKind: JsonValueKind.Object } audioObject
            && audioObject.TryGetProperty("content_type", out var ctEl))
        {
            var ct = ctEl.GetString();
            if (!string.IsNullOrWhiteSpace(ct))
            {
                var (_, fmt) = InferFromMimeType(ct);
                return (ct, fmt);
            }
        }

        // Then use HTTP content-type.
        if (!string.IsNullOrWhiteSpace(httpContentType))
        {
            var (_, fmt) = InferFromMimeType(httpContentType);
            return (httpContentType, fmt);
        }

        if (audioFileEl is { ValueKind: JsonValueKind.Object } audioObjectWithName
            && audioObjectWithName.TryGetProperty("file_name", out var fileNameEl)
            && fileNameEl.ValueKind == JsonValueKind.String)
        {
            var fileName = fileNameEl.GetString();
            var ext = Path.GetExtension(fileName)?.Trim('.').ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(ext))
                return InferFromFormat(ext);
        }

        if (!string.IsNullOrWhiteSpace(requestedFormat))
        {
            var inferred = InferFromFormat(requestedFormat);
            if (inferred.Format != "bin")
                return inferred;
        }

        // Finally infer from URL extension.
        var extFromUrl = GetUrlExtension(audioUrl);
        return extFromUrl switch
        {
            "mp3" => ("audio/mpeg", "mp3"),
            "wav" => ("audio/wav", "wav"),
            "aac" => ("audio/aac", "aac"),
            "flac" => ("audio/flac", "flac"),
            "opus" => ("audio/opus", "opus"),
            _ => ("application/octet-stream", extFromUrl is null or "" ? "bin" : extFromUrl)
        };
    }

    private static (string MimeType, string Format) InferFromMimeType(string mimeType)
    {
        var lowered = mimeType.ToLowerInvariant();

        if (lowered.Contains("mpeg") || lowered.Contains("mp3"))
            return (mimeType, "mp3");
        if (lowered.Contains("wav"))
            return (mimeType, "wav");
        if (lowered.Contains("aac"))
            return (mimeType, "aac");
        if (lowered.Contains("flac"))
            return (mimeType, "flac");
        if (lowered.Contains("opus"))
            return (mimeType, "opus");
        if (lowered.Contains("pcm") || lowered.Contains("linear16") || lowered.Contains("mulaw") || lowered.Contains("alaw"))
            return (mimeType, "pcm");

        return (mimeType, "bin");
    }

    private static (string MimeType, string Format) InferFromFormat(string format)
    {
        var normalized = format.Trim().ToLowerInvariant();
        var formatPrefix = normalized.Split('_', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalized;

        return formatPrefix switch
        {
            "mp3" => ("audio/mpeg", "mp3"),
            "wav" => ("audio/wav", "wav"),
            "aac" => ("audio/aac", "aac"),
            "flac" => ("audio/flac", "flac"),
            "opus" or "ogg" => ("audio/opus", "opus"),
            "pcm" or "linear16" or "mulaw" or "alaw" => ("audio/pcm", formatPrefix),
            _ => ("application/octet-stream", string.IsNullOrWhiteSpace(normalized) ? "bin" : normalized)
        };
    }

    private static string? GetUrlExtension(string? audioUrl)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
            return null;

        var path = Uri.TryCreate(audioUrl, UriKind.Absolute, out var uri)
            ? uri.AbsolutePath
            : audioUrl.Split('?', '#')[0];

        return Path.GetExtension(path)?.Trim('.').ToLowerInvariant();
    }

}
