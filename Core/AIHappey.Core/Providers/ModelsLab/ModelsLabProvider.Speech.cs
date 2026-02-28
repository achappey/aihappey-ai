using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.ModelsLab;

public partial class ModelsLabProvider
{
    private async Task<SpeechResponse> SpeechRequestTextToAudio(SpeechRequest request, CancellationToken cancellationToken = default)
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
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });
        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });
        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            warnings.Add(new { type = "unsupported", feature = "outputFormat" });

        var apiKey = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"No {nameof(ModelsLab)} API key.");

        var effectiveModel = request.Model;
        var endpoint = ResolveSpeechEndpoint(effectiveModel);

        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey,
            ["prompt"] = request.Text
        };

        MergeRawProviderOptions(payload, request.ProviderOptions);

        // Keep reserved keys deterministic after raw passthrough merge.
        payload["key"] = apiKey;
        payload["prompt"] = request.Text;

        var started = await PostModelsLabSpeechJsonAsync(endpoint, payload, cancellationToken);
        ThrowIfModelsLabSpeechError(started, "speech generation");

        var completed = started.Clone();
        var status = GetStatus(completed);
        if (!string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
        {
            var requestId = GetRequestId(completed)
                ?? throw new InvalidOperationException("ModelsLab speech request did not return an id for polling.");

            var fetchResult = GetFetchResultUrl(completed);

            completed = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
                poll: ct => FetchSpeechStatusAsync(fetchResult, requestId, apiKey, ct),
                isTerminal: root => IsSpeechTerminalStatus(GetStatus(root)),
                interval: TimeSpan.FromSeconds(5),
                timeout: TimeSpan.FromMinutes(10),
                maxAttempts: null,
                cancellationToken: cancellationToken);

            ThrowIfModelsLabSpeechError(completed, "speech fetch");

            var finalStatus = GetStatus(completed);
            if (!string.Equals(finalStatus, "success", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"ModelsLab speech generation did not complete successfully. Status: {finalStatus ?? "unknown"}.");
        }

        var outputValue = GetFirstSpeechOutputValue(completed)
            ?? throw new InvalidOperationException("ModelsLab speech generation returned no output.");

        var (base64Data, mediaType) = await ResolveSpeechOutputAsync(outputValue, cancellationToken);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = completed.Clone()
        };

        if (completed.TryGetProperty("meta", out var metaEl))
            providerMetadata["meta"] = metaEl.Clone();

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = base64Data,
                MimeType = mediaType,
                Format = MapMimeToAudioFormat(mediaType)
            },
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = completed.Clone()
            }
        };
    }

    private static string ResolveSpeechEndpoint(string model)
        => model.ToLowerInvariant() switch
        {
            "music_gen" => "api/v6/voice/music_gen",
            "sfx" => "api/v6/voice/sfx",
            _ => throw new NotSupportedException($"ModelsLab speech model '{model}' is not supported. Use 'music_gen' or 'sfx'.")
        };

    private async Task<JsonElement> PostModelsLabSpeechJsonAsync(string endpoint, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(payload, ImageJsonOptions);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"ModelsLab API error: {(int)resp.StatusCode} {resp.StatusCode}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        return doc.RootElement.Clone();
    }

    private async Task<JsonElement> FetchSpeechStatusAsync(string? fetchResult, long requestId, string apiKey, CancellationToken cancellationToken)
    {
        var payload = new Dictionary<string, object?>
        {
            ["key"] = apiKey
        };

        if (!string.IsNullOrWhiteSpace(fetchResult))
        {
            try
            {
                return await PostModelsLabSpeechJsonAsync(fetchResult, payload, cancellationToken);
            }
            catch
            {
                // Fallback to deterministic fetch endpoint below.
            }
        }

        return await PostModelsLabSpeechJsonAsync($"api/v6/voice/fetch/{requestId}", payload, cancellationToken);
    }

    private static bool IsSpeechTerminalStatus(string? status)
        => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase);

    private static void ThrowIfModelsLabSpeechError(JsonElement root, string operation)
    {
        var status = GetStatus(root);
        if (!string.Equals(status, "error", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase))
            return;

        var message = root.TryGetProperty("message", out var messageEl) && messageEl.ValueKind == JsonValueKind.String
            ? messageEl.GetString()
            : "Unknown ModelsLab error.";

        throw new Exception($"ModelsLab {operation} error: {message}");
    }

    private static string? GetFetchResultUrl(JsonElement root)
        => root.TryGetProperty("fetch_result", out var fetchEl) && fetchEl.ValueKind == JsonValueKind.String
            ? fetchEl.GetString()
            : null;

    private static string? GetFirstSpeechOutputValue(JsonElement root)
    {
        foreach (var key in new[] { "output", "proxy_links", "links", "future_links" })
        {
            if (!root.TryGetProperty(key, out var arrEl) || arrEl.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in arrEl.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String)
                    continue;

                var value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
        }

        return null;
    }

    private async Task<(string Base64Data, string MediaType)> ResolveSpeechOutputAsync(string output, CancellationToken cancellationToken)
    {
        if (output.StartsWith("data:audio/", StringComparison.OrdinalIgnoreCase))
        {
            var mediaTypeEnd = output.IndexOf(';');
            var commaIndex = output.IndexOf(',');

            if (mediaTypeEnd > 5 && commaIndex > mediaTypeEnd)
            {
                var mediaType = output[5..mediaTypeEnd];
                var dataPart = output[(commaIndex + 1)..];
                return (dataPart, string.IsNullOrWhiteSpace(mediaType) ? "audio/mpeg" : mediaType);
            }
        }

        if (Uri.TryCreate(output, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            using var resp = await _client.GetAsync(uri, cancellationToken);
            var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
            {
                var err = Encoding.UTF8.GetString(bytes);
                throw new Exception($"ModelsLab speech download failed: {(int)resp.StatusCode} {resp.StatusCode}: {err}");
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType
                ?? GuessAudioMediaType(output)
                ?? "audio/mpeg";

            return (Convert.ToBase64String(bytes), mediaType);
        }

        return (output, "audio/mpeg");
    }

    private static string? GuessAudioMediaType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var withoutQuery = value.Split('?', '#')[0];
        if (withoutQuery.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)) return "audio/mpeg";
        if (withoutQuery.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return "audio/wav";
        if (withoutQuery.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)) return "audio/ogg";
        if (withoutQuery.EndsWith(".opus", StringComparison.OrdinalIgnoreCase)) return "audio/opus";
        if (withoutQuery.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)) return "audio/flac";
        if (withoutQuery.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)) return "audio/aac";
        if (withoutQuery.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)) return "audio/mp4";
        if (withoutQuery.EndsWith(".webm", StringComparison.OrdinalIgnoreCase)) return "audio/webm";

        return null;
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
}

