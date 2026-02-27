using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runpod;

public partial class RunpodProvider
{
    private async Task<SpeechResponse> RunpodSpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = NormalizeRunpodModelId(request.Model);
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var input = BuildRunpodSpeechInput(request);
        var passthroughInput = TryGetRunpodPassthroughInput(request);
        MergeJsonObject(input, passthroughInput);

        var payload = new JsonObject
        {
            ["input"] = input
        };

        var route = $"{model}/runsync";
        var payloadJson = payload.ToJsonString(JsonSerializerOptions.Web);

        using var submitResp = await _client.PostAsync(
            route,
            new StringContent(payloadJson, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var submitRaw = await submitResp.Content.ReadAsStringAsync(cancellationToken);
        if (!submitResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Runpod speech request failed ({(int)submitResp.StatusCode}): {submitRaw}");

        using var submitDoc = JsonDocument.Parse(submitRaw);
        var root = submitDoc.RootElement;

        var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        if (string.Equals(status, "FAILED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "CANCELLED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(status, "TIMED_OUT", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Runpod speech generation failed with status '{status}': {root.GetRawText()}");
        }

        var audioUrl = ExtractRunpodSpeechAudioUrl(root);
        if (string.IsNullOrWhiteSpace(audioUrl))
            throw new InvalidOperationException("Runpod speech response did not contain output.audio_url.");

        using var mediaResp = await _client.GetAsync(audioUrl, cancellationToken);
        var bytes = await mediaResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!mediaResp.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"Runpod speech download failed ({(int)mediaResp.StatusCode}): {text}");
        }

        var outputFormat = ResolveRunpodSpeechFormat(request.OutputFormat, passthroughInput, audioUrl);
        var mimeType = mediaResp.Content.Headers.ContentType?.MediaType
            ?? GuessRunpodSpeechMimeType(outputFormat, audioUrl)
            ?? "application/octet-stream";

        var runpodMetadata = new Dictionary<string, JsonElement>
        {
            ["audio_url"] = JsonSerializer.SerializeToElement(audioUrl, JsonSerializerOptions.Web),
            ["resolved_input"] = JsonSerializer.SerializeToElement(input, JsonSerializerOptions.Web)
        };

        if (passthroughInput is not null)
            runpodMetadata["passthrough_input"] = JsonSerializer.SerializeToElement(passthroughInput, JsonSerializerOptions.Web);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = outputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(runpodMetadata, JsonSerializerOptions.Web)
            },
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }

    private static JsonObject BuildRunpodSpeechInput(SpeechRequest request)
    {
        var input = new JsonObject
        {
            ["prompt"] = request.Text
        };

        if (!string.IsNullOrWhiteSpace(request.Voice))
            input["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            input["format"] = request.OutputFormat.Trim().ToLowerInvariant();

        return input;
    }

    private static JsonObject? TryGetRunpodPassthroughInput(SpeechRequest request)
    {
        if (request.ProviderOptions is null)
            return null;

        if (!request.ProviderOptions.TryGetValue("runpod", out var runpod))
            return null;

        if (runpod.ValueKind != JsonValueKind.Object)
            return null;

        if (!runpod.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object)
            return null;

        return JsonNode.Parse(input.GetRawText()) as JsonObject;
    }

    private static string ResolveRunpodSpeechFormat(string? requestFormat, JsonObject? passthroughInput, string? audioUrl)
    {
        if (!string.IsNullOrWhiteSpace(requestFormat))
            return requestFormat.Trim().ToLowerInvariant();

        if (passthroughInput is not null
            && passthroughInput.TryGetPropertyValue("format", out var formatNode)
            && formatNode is JsonValue formatValue
            && formatValue.TryGetValue<string>(out var format)
            && !string.IsNullOrWhiteSpace(format))
        {
            return format.Trim().ToLowerInvariant();
        }

        return GuessRunpodSpeechFormatFromUrl(audioUrl) ?? "wav";
    }

    private static string? GuessRunpodSpeechFormatFromUrl(string? audioUrl)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
            return null;

        if (audioUrl.EndsWith(".flac", StringComparison.OrdinalIgnoreCase))
            return "flac";

        if (audioUrl.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase))
            return "ogg";

        if (audioUrl.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            return "wav";

        if (audioUrl.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            return "mp3";

        return null;
    }

    private static string? GuessRunpodSpeechMimeType(string? format, string? audioUrl)
    {
        var fmt = format?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(fmt))
            fmt = GuessRunpodSpeechFormatFromUrl(audioUrl);

        return fmt switch
        {
            "wav" => "audio/wav",
            "flac" => "audio/flac",
            "ogg" => "audio/ogg",
            "mp3" => "audio/mpeg",
            _ => null
        };
    }

    private static string? ExtractRunpodSpeechAudioUrl(JsonElement root)
    {
        if (root.TryGetProperty("output", out var outputEl)
            && outputEl.ValueKind == JsonValueKind.Object
            && outputEl.TryGetProperty("audio_url", out var audioUrlEl)
            && audioUrlEl.ValueKind == JsonValueKind.String
            && TryGetUrl(audioUrlEl.GetString(), out var directAudioUrl))
        {
            return directAudioUrl;
        }

        if (root.TryGetProperty("audio_url", out var rootAudioUrlEl)
            && rootAudioUrlEl.ValueKind == JsonValueKind.String
            && TryGetUrl(rootAudioUrlEl.GetString(), out var rootAudioUrl))
        {
            return rootAudioUrl;
        }

        return FindFirstAudioUrl(root);
    }

    private static string? FindFirstAudioUrl(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.Name.Equals("audio_url", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("audioUrl", StringComparison.OrdinalIgnoreCase)
                         || property.Name.Equals("url", StringComparison.OrdinalIgnoreCase))
                        && property.Value.ValueKind == JsonValueKind.String
                        && TryGetUrl(property.Value.GetString(), out var url))
                    {
                        return url;
                    }

                    var nested = FindFirstAudioUrl(property.Value);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindFirstAudioUrl(item);
                    if (!string.IsNullOrWhiteSpace(nested))
                        return nested;
                }
                break;

            case JsonValueKind.String:
                if (TryGetUrl(element.GetString(), out var candidate)
                    && (candidate.Contains("audio", StringComparison.OrdinalIgnoreCase)
                        || candidate.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                        || candidate.EndsWith(".flac", StringComparison.OrdinalIgnoreCase)
                        || candidate.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase)
                        || candidate.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
                break;
        }

        return null;
    }
}
