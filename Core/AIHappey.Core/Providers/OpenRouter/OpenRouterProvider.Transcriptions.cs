using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.OpenRouter;

public partial class OpenRouterProvider
{
    private static readonly JsonSerializerOptions OpenRouterTranscriptionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<TranscriptionResponse> TranscriptionRequestOpenRouter(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var now = DateTime.UtcNow;
        var payload = BuildOpenRouterTranscriptionPayload(request);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, OpenRouterTranscriptionJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenRouter transcription request failed ({(int)resp.StatusCode})."
                : $"OpenRouter transcription request failed ({(int)resp.StatusCode}): {raw}");

        return ConvertOpenRouterTranscriptionResponse(raw, request.Model, now, request, payload, resp);
    }

    private static Dictionary<string, object?> BuildOpenRouterTranscriptionPayload(TranscriptionRequest request)
    {
        var audioBase64 = ReadOpenRouterTranscriptionAudioBase64(request);
        var format = ResolveOpenRouterTranscriptionAudioFormat(request.MediaType);

        var payload = new Dictionary<string, object?>
        {
            ["input_audio"] = new Dictionary<string, object?>
            {
                ["data"] = audioBase64,
                ["format"] = format
            },
            ["model"] = request.Model
        };

        MergeOpenRouterTranscriptionProviderOptions(payload, request);

        return payload;
    }

    private static void MergeOpenRouterTranscriptionProviderOptions(Dictionary<string, object?> payload, TranscriptionRequest request)
    {
        if (request.ProviderOptions is null)
            return;

        if (!request.ProviderOptions.TryGetValue("openrouter", out var providerOptions))
            return;

        if (providerOptions.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in providerOptions.EnumerateObject())
            payload[property.Name] = property.Value.Clone();
    }

    private TranscriptionResponse ConvertOpenRouterTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        TranscriptionRequest request,
        Dictionary<string, object?> payload,
        HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var text = ReadOpenRouterTranscriptionString(root, "text") ?? string.Empty;
        var usage = root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
            ? usageEl.Clone()
            : (JsonElement?)null;

        return new TranscriptionResponse
        {
            Text = text,
            DurationInSeconds = ReadOpenRouterTranscriptionFloat(usage, "seconds"),
            ProviderMetadata = BuildOpenRouterTranscriptionProviderMetadata(request, payload, response, root),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = model,
                Body = root
            }
        };
    }

    private Dictionary<string, JsonElement> BuildOpenRouterTranscriptionProviderMetadata(
        TranscriptionRequest request,
        Dictionary<string, object?> payload,
        HttpResponseMessage response,
        JsonElement responseRoot)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["request"] = payload,
            ["response"] = responseRoot,
            ["statusCode"] = (int)response.StatusCode
        };

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue("openrouter", out var rawOptions)
            && rawOptions.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            metadata["providerOptions"] = rawOptions.Clone();
        }

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, OpenRouterTranscriptionJsonOptions)
        };
    }

    private static string ReadOpenRouterTranscriptionAudioBase64(TranscriptionRequest request)
    {
        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } el => el.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        try
        {
            _ = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        return audioString;
    }

    private static string ResolveOpenRouterTranscriptionAudioFormat(string mediaType)
    {
        if (string.IsNullOrWhiteSpace(mediaType))
            throw new ArgumentException("MediaType is required.", nameof(mediaType));

        var normalized = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "audio/mpeg" => "mp3",
            "audio/mp3" => "mp3",
            "audio/wav" => "wav",
            "audio/x-wav" => "wav",
            "audio/wave" => "wav",
            "audio/flac" => "flac",
            "audio/x-flac" => "flac",
            "audio/mp4" => "m4a",
            "audio/x-m4a" => "m4a",
            "audio/ogg" => "ogg",
            "audio/webm" => "webm",
            "audio/aac" => "aac",
            _ => mediaType.GetAudioExtension().TrimStart('.')
        };
    }

    private static string? ReadOpenRouterTranscriptionString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
               && element.TryGetProperty(propertyName, out var property)
               && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static float? ReadOpenRouterTranscriptionFloat(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number)
            return (float)property.GetDouble();

        if (property.ValueKind == JsonValueKind.String
            && float.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
