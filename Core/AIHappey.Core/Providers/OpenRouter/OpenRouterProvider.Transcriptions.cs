using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using System.Net.Mime;
using System.Runtime.CompilerServices;
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

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);

        if (string.IsNullOrWhiteSpace(request.Audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        var mediaType = ResolveOpenRouterStreamingTranscriptionMediaType(request.InputAudioFormat?.Type);

        // OpenRouter has no native streaming STT; run the batch request and replay it as stream parts.
        var response = await TranscriptionRequest(new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = mediaType,
            ProviderOptions = request.ProviderOptions
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var warnings = request.InputAudioFormat?.Rate is null
            ? []
            : new List<object> { new { type = "unsupported", feature = "inputAudioFormat.rate" } };

        yield return new TranscriptionStreamStartPart { Warnings = warnings };
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp,
            ModelId = response.Response.ModelId,
            Headers = response.Response.Headers,
            Body = response.Response.Body
        };

        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = response.Response.Body };

        var segments = response.Segments.ToList();
        for (var i = 0; i < segments.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            yield return new TranscriptionFinalPart
            {
                Id = $"segment_{i}",
                Text = segments[i].Text,
                StartSecond = segments[i].StartSecond,
                EndSecond = segments[i].EndSecond
            };
        }

        if (!string.IsNullOrEmpty(response.Text))
        {
            yield return new TranscriptionDeltaPart
            {
                Delta = response.Text,
                ProviderMetadata = response.ProviderMetadata
            };
        }

        yield return new TranscriptionFinishPart
        {
            Text = response.Text,
            Segments = segments,
            Language = response.Language,
            DurationInSeconds = response.DurationInSeconds,
            ProviderMetadata = response.ProviderMetadata
        };
    }

    private static string ResolveOpenRouterStreamingTranscriptionMediaType(string? format)
    {
        if (string.IsNullOrWhiteSpace(format))
            throw new ArgumentException("InputAudioFormat.Type is required.", nameof(format));

        var normalized = format.Split(';', 2)[0].Trim().ToLowerInvariant();

        return normalized switch
        {
            "mp3" or "mpeg" or "audio/mpeg" or "audio/mp3" => "audio/mpeg",
            "wav" or "wave" or "audio/wav" or "audio/x-wav" or "audio/wave" => "audio/wav",
            "flac" or "audio/flac" or "audio/x-flac" => "audio/flac",
            "m4a" or "mp4" or "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "audio/mp4",
            "ogg" or "opus" or "audio/ogg" or "audio/opus" => "audio/ogg",
            "webm" or "audio/webm" => "audio/webm",
            "aac" or "audio/aac" => "audio/aac",
            "pcm" or "pcm16" or "pcm24" or "l16" or "audio/pcm" or "audio/l16" => throw new NotSupportedException(
                "OpenRouter transcription requires a containerized audio format (wav, mp3, flac, m4a, ogg, webm or aac); raw PCM is not supported."),
            _ => throw new NotSupportedException($"OpenRouter transcription does not support input audio format '{format}'.")
        };
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
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
        var requestBody = JsonSerializer.Serialize(payload, OpenRouterTranscriptionJsonOptions);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = new StringContent(
                requestBody,
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpenRouter transcription request failed ({(int)resp.StatusCode})."
                : $"OpenRouter transcription request failed ({(int)resp.StatusCode}): {raw}");

        return ConvertOpenRouterTranscriptionResponse(raw, request.Model,
            now, requestBody, resp.GetHeaders());
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


    private static decimal? ReadOpenRouterTranscriptionDecimal(JsonElement? element, string propertyName)
    {
        if (element is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number)
            return property.GetDecimal();

        if (property.ValueKind == JsonValueKind.String
            && decimal.TryParse(
                property.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private Dictionary<string, JsonElement> BuildOpenRouterTranscriptionProviderMetadata(
        JsonElement? usage,
        string? generationId = null)
    {
        var cost = ReadOpenRouterTranscriptionDecimal(usage, "cost");

        return GetIdentifier()
            .CreatePrimitiveProviderMetadata(new
            {
                usage,
                generationId
            }, cost);
    }

    private TranscriptionResponse ConvertOpenRouterTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        string requestBody,
        IDictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        var text = ReadOpenRouterTranscriptionString(root, "text") ?? string.Empty;

        var usage = root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object
            ? usageEl.Clone()
            : (JsonElement?)null;

        var segments = ReadOpenRouterTranscriptionSegments(root);
        if (string.IsNullOrEmpty(text) && segments.Count > 0)
            text = string.Join(" ", segments.Select(segment => segment.Text.Trim()).Where(t => t.Length > 0));

        var generationId = headers
            .FirstOrDefault(h => h.Key.Equals("X-Generation-Id", StringComparison.OrdinalIgnoreCase)).Value;

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadOpenRouterTranscriptionString(root, "language"),
            DurationInSeconds = ReadOpenRouterTranscriptionFloat(root, "duration")
                ?? ReadOpenRouterTranscriptionFloat(usage, "seconds"),
            Segments = segments,
            ProviderMetadata = BuildOpenRouterTranscriptionProviderMetadata(usage, generationId),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }

    private static List<TranscriptionSegment> ReadOpenRouterTranscriptionSegments(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("segments", out var segments)
            || segments.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<TranscriptionSegment>();
        foreach (var segment in segments.EnumerateArray())
        {
            var segmentText = ReadOpenRouterTranscriptionString(segment, "text");
            if (segmentText is null)
                continue;

            result.Add(new TranscriptionSegment
            {
                Text = segmentText,
                StartSecond = ReadOpenRouterTranscriptionFloat(segment, "start") ?? 0,
                EndSecond = ReadOpenRouterTranscriptionFloat(segment, "end") ?? 0
            });
        }

        return result;
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
