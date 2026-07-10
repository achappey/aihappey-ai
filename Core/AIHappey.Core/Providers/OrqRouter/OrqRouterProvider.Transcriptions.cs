using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider
{
    private async Task<TranscriptionResponse> OrqRouterTranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var audioString = request.Audio switch
        {
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        var bytes = DecodeOrqRouterBase64Payload(audioString);
        var mediaType = NormalizeOrqRouterMediaType(request.MediaType, "audio/mpeg");
        var providerOptions = ReadOrqRouterProviderOptions(request.ProviderOptions);
        using var form = new MultipartFormDataContent();

        AddOrqRouterMultipartString(form, "model", request.Model);

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", "audio" + mediaType.GetAudioExtension());

        AddOrqRouterMultipartProviderOptions(form, providerOptions, ReservedOrqRouterTranscriptionKeys);

        using var response = await _client.PostAsync("v2/router/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OrqRouter transcription request failed ({(int)response.StatusCode})."
                : $"OrqRouter transcription request failed ({(int)response.StatusCode}): {raw}");

        return ConvertOrqRouterTranscriptionResponse(raw, request.Model, now);
    }

    private static readonly HashSet<string> ReservedOrqRouterTranscriptionKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "model"
    };

    private static TranscriptionResponse ConvertOrqRouterTranscriptionResponse(string raw, string model, DateTime timestamp)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TranscriptionResponse
            {
                Text = string.Empty,
                Segments = [],
                ProviderMetadata = ProviderId.CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    ModelId = model.ToModelId(ProviderId),
                    Body = raw
                }
            };
        }

        var trimmed = raw.TrimStart();
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Segments = [],
                ProviderMetadata = ProviderId.CreatePrimitiveProviderMetadata(new { text = raw }),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    ModelId = model.ToModelId(ProviderId),
                    Body = raw
                }
            };
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();

        return new TranscriptionResponse
        {
            Text = ReadOrqRouterString(root, "text") ?? string.Empty,
            Language = ReadOrqRouterString(root, "language"),
            DurationInSeconds = ReadOrqRouterFloat(root, "duration"),
            Segments = ExtractOrqRouterTranscriptionSegments(root),
            ProviderMetadata = BuildOrqRouterProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = model.ToModelId(ProviderId),
                Body = root
            }
        };
    }

    private static List<TranscriptionSegment> ExtractOrqRouterTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return [];

        return segmentsEl
            .EnumerateArray()
            .Where(segment => segment.ValueKind == JsonValueKind.Object)
            .Select(segment => new TranscriptionSegment
            {
                Text = ReadOrqRouterString(segment, "text") ?? string.Empty,
                StartSecond = ReadOrqRouterFloat(segment, "start") ?? 0f,
                EndSecond = ReadOrqRouterFloat(segment, "end") ?? 0f
            })
            .ToList();
    }
}
