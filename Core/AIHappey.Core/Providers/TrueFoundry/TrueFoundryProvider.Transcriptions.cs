using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;

namespace AIHappey.Core.Providers.TrueFoundry;

public partial class TrueFoundryProvider
{
    private async Task<TranscriptionResponse> TrueFoundryTranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
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

        var bytes = Convert.FromBase64String(audioString.RemoveDataUrlPrefix());
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var requestFields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
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
        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        AddTrueFoundryTranscriptionMetadataFormFields(form, metadata, requestFields);

        using var response = await _client.PostAsync("audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"TrueFoundry transcription request failed ({(int)response.StatusCode})."
                : $"TrueFoundry transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertTrueFoundryTranscriptionResponse(
            raw,
            request.Model,
            now,
            warnings,
            response.GetHeaders(),
            requestFields);
    }

    private void AddTrueFoundryTranscriptionMetadataFormFields(
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

            var value = TrueFoundryJsonElementToFormValue(property.Value);
            if (value is null)
                continue;

            requestFields[property.Name] = value;
            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private TranscriptionResponse ConvertTrueFoundryTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        IDictionary<string, string>? headers,
        Dictionary<string, object?> requestFields)
    {
        if (!TryParseTrueFoundryJson(raw, out var document))
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
            var root = document.RootElement.Clone();
            var segments = ParseTrueFoundryTranscriptionSegments(root);
            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(segment => segment.Text));

            return new TranscriptionResponse
            {
                Text = text,
                Language = TrueFoundryTryGetString(root, "language"),
                DurationInSeconds = root.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number
                    ? (float)durationElement.GetDouble()
                    : null,
                Segments = segments,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Body = root
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, JsonSerializerOptions.Web)
                }
            };
        }
    }

    private bool TryParseTrueFoundryJson(string raw, out JsonDocument document)
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

    private List<TranscriptionSegment> ParseTrueFoundryTranscriptionSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (!root.TryGetProperty("segments", out var segmentsElement) || segmentsElement.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var segment in segmentsElement.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
                continue;

            var text = TrueFoundryTryGetString(segment, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = (float)(TrueFoundryTryGetDouble(segment, "start", "start_second", "startSecond") ?? 0d);
            var end = (float)(TrueFoundryTryGetDouble(segment, "end", "end_second", "endSecond") ?? start);
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
}
