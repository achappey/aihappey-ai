using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ILMU;

public partial class ILMUProvider
{
    private async Task<TranscriptionResponse> ILMUTranscriptionRequest(
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

        var bytes = Convert.FromBase64String(audioString.RemoveDataUrlPrefix());
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var requestFields = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["file"] = new
            {
                fileName,
                mediaType = request.MediaType,
                bytes = bytes.LongLength
            }
        };

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        AddILMUMultipartProviderOptions(form, metadata, requestFields, "file", "model");

        form.Add(new StringContent(request.Model, Encoding.UTF8), "model");
        requestFields["model"] = request.Model;

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"ILMU transcription request failed ({(int)response.StatusCode})."
                : $"ILMU transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertILMUTranscriptionResponse(
            raw,
            request.Model,
            now,
            warnings,
            response.GetHeaders(),
            requestFields);
    }

    private TranscriptionResponse ConvertILMUTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IEnumerable<object> warnings,
        IDictionary<string, string>? headers,
        Dictionary<string, object?> requestFields)
    {
        if (!ILMUTryParseJson(raw, out var document))
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
                    Body = JsonSerializer.Serialize(requestFields, ILMUJsonOptions)
                }
            };
        }

        using (document)
        {
            var root = document.RootElement.Clone();
            var segments = ParseILMUTranscriptionSegments(root);
            var text = root.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String
                ? textElement.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(segment => segment.Text));

            return new TranscriptionResponse
            {
                Text = text,
                Language = ILMUTryGetString(root, "language"),
                DurationInSeconds = ILMUTryGetFloat(root, "duration"),
                Segments = segments,
                Warnings = warnings,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Body = root
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, ILMUJsonOptions)
                }
            };
        }
    }

    private static List<TranscriptionSegment> ParseILMUTranscriptionSegments(JsonElement root)
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

            var start = ILMUTryGetFloat(segment, "start", "start_second", "startSecond") ?? 0f;
            var end = ILMUTryGetFloat(segment, "end", "end_second", "endSecond") ?? start;
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
