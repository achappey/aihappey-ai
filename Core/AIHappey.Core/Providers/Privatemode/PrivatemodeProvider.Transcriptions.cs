using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Privatemode;

public partial class PrivatemodeProvider
{
    private const string PrivatemodeTranscriptionsEndpoint = "v1/audio/transcriptions";

    private static readonly HashSet<string> ReservedTranscriptionFormKeys =
    [
        "model",
        "file",
        "audio",
        "mediaType",
        "media_type",
        "prompt",
        "language",
        "response_format"
    ];

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

        var audioString = request.Audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;
        List<object> warnings = [];

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        string? appliedPrompt = null;
        string? appliedLanguage = null;
        string? appliedResponseFormat = null;
        List<string> passthroughApplied = [];

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (property.NameEquals("prompt") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var prompt = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(prompt))
                    {
                        appliedPrompt = prompt;
                        form.Add(new StringContent(prompt), "prompt");
                    }

                    continue;
                }

                if (property.NameEquals("language") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var language = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(language))
                    {
                        appliedLanguage = language;
                        form.Add(new StringContent(language), "language");
                    }

                    continue;
                }

                if (property.NameEquals("response_format") && property.Value.ValueKind == JsonValueKind.String)
                {
                    var responseFormat = property.Value.GetString();
                    if (!string.IsNullOrWhiteSpace(responseFormat))
                    {
                        appliedResponseFormat = responseFormat;
                        form.Add(new StringContent(responseFormat), "response_format");
                    }

                    continue;
                }

                if (ReservedTranscriptionFormKeys.Contains(property.Name))
                    continue;

                if (TryAddPassthroughFormValues(form, property, warnings))
                    passthroughApplied.Add(property.Name);
            }
        }

        using var resp = await _client.PostAsync(PrivatemodeTranscriptionsEndpoint, form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Privatemode transcription request failed ({(int)resp.StatusCode}): {raw}");

        string text;
        string? languageFromResponse = null;
        float? duration = null;
        List<TranscriptionSegment> segments = [];
        object responseBody;
        string modelId = request.Model;

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                ? textEl.GetString() ?? string.Empty
                : string.Empty;

            languageFromResponse = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
                ? languageEl.GetString()
                : null;

            if (TryReadFlexibleFloat(root, "duration", out var responseDuration))
                duration = responseDuration;

            if (!duration.HasValue &&
                root.TryGetProperty("usage", out var usageEl) &&
                usageEl.ValueKind == JsonValueKind.Object &&
                TryReadFlexibleFloat(usageEl, "seconds", out var usageSeconds))
            {
                duration = usageSeconds;
            }

            if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsEl.EnumerateArray())
                {
                    if (segment.ValueKind != JsonValueKind.Object)
                        continue;

                    var segmentText = segment.TryGetProperty("text", out var segmentTextEl) && segmentTextEl.ValueKind == JsonValueKind.String
                        ? segmentTextEl.GetString() ?? string.Empty
                        : string.Empty;

                    var start = TryReadFloat(segment, "start", "start_second", "startSecond");
                    var end = TryReadFloat(segment, "end", "end_second", "endSecond");

                    if (end < start)
                        end = start;

                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }

            if (string.IsNullOrWhiteSpace(text))
                text = string.Join(" ", segments.Select(s => s.Text));

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
                modelId = modelEl.GetString() ?? request.Model;

            responseBody = JsonSerializer.Deserialize<object>(raw, JsonSerializerOptions.Web) ?? raw;
        }
        catch (JsonException)
        {
            text = raw;
            responseBody = raw;
        }

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint = PrivatemodeTranscriptionsEndpoint,
                model = request.Model,
                prompt = appliedPrompt,
                language = appliedLanguage,
                response_format = appliedResponseFormat,
                passthrough = passthroughApplied
            })
        };

        return new TranscriptionResponse
        {
            ProviderMetadata = providerMetadata,
            Text = text,
            Language = languageFromResponse,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = modelId,
                Body = responseBody
            }
        };
    }

    private static bool TryAddPassthroughFormValues(
        MultipartFormDataContent form,
        JsonProperty property,
        ICollection<object> warnings)
    {
        static string? ToRawString(JsonElement value)
            => value.ValueKind switch
            {
                JsonValueKind.String => value.GetString(),
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
                JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
                _ => null
            };

        var scalar = ToRawString(property.Value);
        if (scalar is not null)
        {
            form.Add(new StringContent(scalar), property.Name);
            return true;
        }

        if (property.Value.ValueKind == JsonValueKind.Array)
        {
            var addedAny = false;

            foreach (var item in property.Value.EnumerateArray())
            {
                var arrayValue = ToRawString(item);
                if (arrayValue is null)
                {
                    warnings.Add(new
                    {
                        type = "unsupported",
                        feature = property.Name,
                        reason = "array item must be string, number, or boolean"
                    });
                    continue;
                }

                form.Add(new StringContent(arrayValue), property.Name);
                addedAny = true;
            }

            return addedAny;
        }

        warnings.Add(new
        {
            type = "unsupported",
            feature = property.Name,
            reason = "value kind must be string, number, boolean, or array of these"
        });

        return false;
    }

    private static bool TryReadFlexibleFloat(JsonElement element, string name, out float value)
    {
        value = 0f;

        if (!element.TryGetProperty(name, out var token))
            return false;

        if (token.ValueKind == JsonValueKind.Number)
        {
            value = (float)token.GetDouble();
            return true;
        }

        if (token.ValueKind == JsonValueKind.String)
            return float.TryParse(token.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);

        return false;
    }

    private static float TryReadFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryReadFlexibleFloat(element, name, out var value))
                return value;
        }

        return 0f;
    }
}

