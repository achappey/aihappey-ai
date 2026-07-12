using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.Apertis;

public partial class ApertisProvider
{
    private async Task<TranscriptionResponse> ApertisTranscriptionRequest(
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
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var audioBase64 = audioString.RemoveDataUrlPrefix();
        var bytes = Convert.FromBase64String(audioBase64);
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var (endpoint, model) = ResolveApertisTranscriptionEndpoint(request.Model);
        var requestFields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["file"] = new
            {
                fileName = "audio" + request.MediaType.GetAudioExtension(),
                mediaType = request.MediaType,
                bytes = bytes.LongLength
            }
        };

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(model, Encoding.UTF8), "model");

        AddRawApertisTranscriptionPassthrough(form, metadata, requestFields);

        using var response = await _client.PostAsync(endpoint, form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Apertis transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertApertisTranscriptionResponse(
            raw,
            request.Model,
            now,
            warnings,
            response.GetHeaders(),
            requestFields);
    }

    private static (string Endpoint, string Model) ResolveApertisTranscriptionEndpoint(string requestModel)
    {
        const string translateSuffix = "/translate";
        var model = requestModel.Trim();

        if (!model.EndsWith(translateSuffix, StringComparison.OrdinalIgnoreCase))
            return ("v1/audio/transcriptions", model);

        var translationModel = model[..^translateSuffix.Length].TrimEnd('/');

        if (string.IsNullOrWhiteSpace(translationModel))
            throw new ArgumentException("Translation model must include a base model before '/translate'.", nameof(requestModel));

        return ("v1/audio/translations", translationModel);
    }

    private static void AddRawApertisTranscriptionPassthrough(
        MultipartFormDataContent form,
        JsonElement metadata,
        Dictionary<string, object?> requestFields)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.NameEquals("file") || property.NameEquals("model"))
                continue;

            var value = ToApertisMultipartValue(property.Value);
            if (value is null)
                continue;

            requestFields[property.Name] = value;
            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static string? ToApertisMultipartValue(JsonElement value)
        => value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Undefined => null,
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => value.GetRawText(),
            JsonValueKind.Object => value.GetRawText(),
            _ => value.GetRawText()
        };

    private TranscriptionResponse ConvertApertisTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        IDictionary<string, string>? headers,
        Dictionary<string, object?> requestFields)
    {
        if (!TryParseApertisJson(raw, out var document))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Body = raw
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, JsonSerializerOptions.Web)
                }
            };
        }

        using (document)
        {
            var root = document.RootElement;
            var segments = ParseApertisTranscriptionSegments(root);
            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(a => a.Text));

            return new TranscriptionResponse
            {
                Text = text,
                Language = root.TryGetProperty("language", out var languageElement) && languageElement.ValueKind == JsonValueKind.String
                    ? languageElement.GetString()
                    : null,
                DurationInSeconds = root.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number
                    ? (float)durationElement.GetDouble()
                    : null,
                Segments = segments,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Body = root.Clone()
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, JsonSerializerOptions.Web)
                }
            };
        }
    }

    private static bool TryParseApertisJson(string raw, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(raw);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }

    private static List<TranscriptionSegment> ParseApertisTranscriptionSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var segment in segmentsElement.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
                continue;

            var text = segment.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = ReadApertisTranscriptionFloat(segment, "start", "start_second", "startSecond");
            var end = ReadApertisTranscriptionFloat(segment, "end", "end_second", "endSecond");

            if (end < start)
                end = start;

            segments.Add(new TranscriptionSegment
            {
                Text = text,
                StartSecond = start,
                EndSecond = end
            });
        }

        return segments;
    }

    private static float ReadApertisTranscriptionFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }
}
