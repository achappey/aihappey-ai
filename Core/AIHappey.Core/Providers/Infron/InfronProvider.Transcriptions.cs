using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Infron;

public partial class InfronProvider
{
    private static readonly Uri InfronTranscriptionUri = new("https://audio.onerouter.pro/v1/audio/transcriptions");

    private static readonly JsonSerializerOptions InfronTranscriptionJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private async Task<TranscriptionResponse> InfronTranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        if (IsInfronTranslationRequested(request.Model, metadata))
            throw new NotSupportedException("Infron audio translations are not supported by this provider yet.");

        var now = DateTime.UtcNow;
        List<object> warnings = [];
        var audioBase64 = ReadInfronTranscriptionAudioBase64(request);
        var bytes = Convert.FromBase64String(audioBase64);
        var fileName = ResolveInfronTranscriptionFileName(request.MediaType, metadata);

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        AddInfronTranscriptionOptions(form, metadata, warnings);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, InfronTranscriptionUri)
        {
            Content = form
        };

        ApplyAuthHeader();

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"Infron transcription request failed ({(int)resp.StatusCode})."
                : $"Infron transcription request failed ({(int)resp.StatusCode}): {raw}");

        return ConvertInfronTranscriptionResponse(raw, request.Model, now, warnings);
    }

    private static void AddInfronTranscriptionOptions(
        MultipartFormDataContent form,
        JsonElement? metadata,
        List<object> warnings)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } options)
            return;

        var applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file",
            "model",
            "endpoint",
            "operation",
            "translation",
            "translate",
            "fileName",
            "filename"
        };

        AddInfronStringFormField(form, options, applied, "prompt");
        AddInfronStringFormField(form, options, applied, "response_format", "responseFormat");
        AddInfronStringFormField(form, options, applied, "language");
        AddInfronNumberFormField(form, options, applied, "temperature");

        if (TryGetInfronTranscriptionArray(options, "timestamp_granularities", "timestampGranularities", out var granularities))
        {
            applied.Add("timestamp_granularities");
            applied.Add("timestampGranularities");

            foreach (var granularity in granularities)
                form.Add(new StringContent(granularity), "timestamp_granularities");
        }

        foreach (var property in options.EnumerateObject())
        {
            if (applied.Contains(property.Name))
                continue;

            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.Object or JsonValueKind.Array)
            {
                warnings.Add(new
                {
                    type = "unsupported",
                    feature = property.Name,
                    details = "Only scalar providerOptions.infron values can be passed to Infron transcription multipart form data."
                });

                continue;
            }

            form.Add(new StringContent(JsonElementToString(property.Value)), property.Name);
        }
    }

    private TranscriptionResponse ConvertInfronTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var segments = ExtractInfronTranscriptionSegments(root).ToList();

        var text = root.TryGetString("text") ?? string.Join(" ", segments.Select(a => a.Text));
        var language = root.TryGetString("language");
        var duration = TryReadInfronFloat(root, "duration", "durationInSeconds");

        if (!duration.HasValue && root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            duration = TryReadInfronFloat(usageEl, "seconds", "duration");

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier()
                .CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = ResolveInfronTimestamp(root, timestamp),
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier())
                     ?? model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private Dictionary<string, JsonElement> BuildInfronTranscriptionProviderMetadata(
        TranscriptionRequest request,
        JsonElement responseRoot,
        HttpResponseMessage response)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["response"] = responseRoot,
            ["statusCode"] = (int)response.StatusCode
        };

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(GetIdentifier(), out var rawOptions)
            && rawOptions.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            metadata["providerOptions"] = rawOptions.Clone();
        }

        return new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(metadata, InfronTranscriptionJsonOptions)
        };
    }

    private static IEnumerable<TranscriptionSegment> ExtractInfronTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var segment in segmentsEl.EnumerateArray())
        {
            var text = segment.TryGetString("text") ?? string.Empty;
            var start = TryReadInfronFloat(segment, "start", "start_second", "startSecond") ?? 0f;
            var end = TryReadInfronFloat(segment, "end", "end_second", "endSecond") ?? start;

            if (end < start)
                end = start;

            yield return new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            };
        }
    }

    private static string ReadInfronTranscriptionAudioBase64(TranscriptionRequest request)
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

    private static string ResolveInfronTranscriptionFileName(string mediaType, JsonElement? metadata)
    {
        var fileName = metadata?.TryGetString("fileName") ?? metadata?.TryGetString("filename");
        if (!string.IsNullOrWhiteSpace(fileName))
            return fileName;

        return "audio" + mediaType.GetAudioExtension();
    }

    private static bool IsInfronTranslationRequested(string model, JsonElement? metadata)
    {
        if (model.EndsWith("/translate", StringComparison.OrdinalIgnoreCase)
            || model.EndsWith("/translation", StringComparison.OrdinalIgnoreCase))
            return true;

        if (metadata is not { ValueKind: JsonValueKind.Object } options)
            return false;

        var endpoint = options.TryGetString("endpoint") ?? options.TryGetString("operation");
        if (endpoint is not null
            && (endpoint.Contains("translation", StringComparison.OrdinalIgnoreCase)
                || endpoint.Contains("translations", StringComparison.OrdinalIgnoreCase)))
            return true;

        return TryReadInfronBoolean(options, "translation", "translate") == true;
    }

    private static void AddInfronStringFormField(
        MultipartFormDataContent form,
        JsonElement options,
        HashSet<string> applied,
        string formName,
        params string[] aliases)
    {
        var names = new[] { formName }.Concat(aliases).ToArray();
        var value = TryGetInfronString(options, names);

        foreach (var name in names)
            applied.Add(name);

        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), formName);
    }

    private static void AddInfronNumberFormField(
        MultipartFormDataContent form,
        JsonElement options,
        HashSet<string> applied,
        string formName,
        params string[] aliases)
    {
        var names = new[] { formName }.Concat(aliases).ToArray();

        foreach (var name in names)
            applied.Add(name);

        foreach (var name in names)
        {
            if (options.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
            {
                form.Add(new StringContent(value.GetDouble().ToString(CultureInfo.InvariantCulture)), formName);
                return;
            }

            if (options.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String)
            {
                var text = value.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    form.Add(new StringContent(text), formName);
                return;
            }
        }
    }

    private static bool TryGetInfronTranscriptionArray(
        JsonElement options,
        string snakeName,
        string camelName,
        out IEnumerable<string> values)
    {
        values = [];

        if (!options.TryGetProperty(snakeName, out var value)
            && !options.TryGetProperty(camelName, out value))
        {
            return false;
        }

        values = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString()!)
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray(),
            JsonValueKind.String when !string.IsNullOrWhiteSpace(value.GetString()) => [value.GetString()!],
            _ => []
        };

        return true;
    }

    private static void MergeInfronProviderOptions(
        Dictionary<string, object?> payload,
        JsonElement? metadata,
        IReadOnlySet<string> excludedNames)
    {
        if (metadata is not { ValueKind: JsonValueKind.Object } metadataElement)
            return;

        foreach (var property in metadataElement.EnumerateObject())
        {
            if (excludedNames.Contains(property.Name))
                continue;

            payload[property.Name] = property.Value.Clone();
        }
    }

    private static bool? TryReadInfronBoolean(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind is JsonValueKind.True)
                return true;

            if (value.ValueKind is JsonValueKind.False)
                return false;

            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed))
                return parsed;
        }

        return null;
    }

    private static float? TryReadInfronFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
                continue;

            if (value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();

            if (value.ValueKind == JsonValueKind.String
                && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static DateTime ResolveInfronTimestamp(JsonElement root, DateTime fallback)
    {
        if (root.TryGetProperty("created", out var createdEl))
        {
            long? unix = createdEl.ValueKind switch
            {
                JsonValueKind.Number when createdEl.TryGetInt64(out var number) => number,
                JsonValueKind.String when long.TryParse(createdEl.GetString(), out var parsed) => parsed,
                _ => null
            };

            if (unix.HasValue)
                return DateTimeOffset.FromUnixTimeSeconds(unix.Value).UtcDateTime;
        }

        return fallback;
    }

    private static string JsonElementToString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Number => value.GetRawText(),
            _ => value.ToString()
        };
    }

    private static string? TryGetInfronString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }
}
