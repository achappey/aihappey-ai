using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TextSynth;

public partial class TextSynthProvider
{
    private async Task<TranscriptionResponse> TextSynthTranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var localModel = ExtractProviderLocalModelId(request.Model);
        if (!string.Equals(localModel, TranscriptionBaseModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"TextSynth transcription model '{request.Model}' is not supported.");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());

        var language = TryGetString(metadata, "language") ?? "auto";
        if (string.IsNullOrWhiteSpace(language))
            language = "auto";

        var audioPayload = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioPayload))
            throw new ArgumentException("Audio base64 payload is required.", nameof(request));

        var bytes = DecodeBase64Payload(audioPayload);

        var mediaType = NormalizeTranscriptionMediaType(request.MediaType);
        var fileName = "input" + GetAudioFileExtension(mediaType);

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(fileContent, "file", fileName);
        form.Add(new StringContent(language), "language");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"v1/engines/{TranscriptionBaseModel}/transcript")
        {
            Content = form
        };

        using var response = await _client.SendAsync(httpRequest, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"TextSynth transcript failed ({(int)response.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var text = TryGetPropertyIgnoreCase(root, "text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        var detectedLanguage = TryGetPropertyIgnoreCase(root, "language", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? langEl.GetString()
            : language;

        float? duration = null;
        if (TryGetPropertyIgnoreCase(root, "duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number
            && durationEl.TryGetSingle(out var durationValue))
        {
            duration = durationValue;
        }

        var segments = new List<TranscriptionSegment>();
        if (TryGetPropertyIgnoreCase(root, "segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var seg in segmentsEl.EnumerateArray())
            {
                if (seg.ValueKind != JsonValueKind.Object)
                    continue;

                var segText = TryGetPropertyIgnoreCase(seg, "text", out var segTextEl) && segTextEl.ValueKind == JsonValueKind.String
                    ? segTextEl.GetString() ?? string.Empty
                    : string.Empty;

                var start = TryGetPropertyIgnoreCase(seg, "start", out var startEl) && startEl.ValueKind == JsonValueKind.Number && startEl.TryGetSingle(out var s)
                    ? s
                    : 0f;

                var end = TryGetPropertyIgnoreCase(seg, "end", out var endEl) && endEl.ValueKind == JsonValueKind.Number && endEl.TryGetSingle(out var e)
                    ? e
                    : start;

                segments.Add(new TranscriptionSegment
                {
                    Text = segText,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = detectedLanguage,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string NormalizeTranscriptionMediaType(string mediaType)
    {
        var normalized = mediaType?.Trim().ToLowerInvariant() ?? string.Empty;

        return normalized switch
        {
            "audio/mpeg" or "audio/mp3" => "audio/mpeg",
            "audio/mp4" or "audio/x-m4a" => "audio/mp4",
            "video/mp4" => "video/mp4",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "audio/wav",
            "audio/opus" or "audio/ogg" => "audio/opus",
            _ => throw new NotSupportedException($"TextSynth transcript supports mp3, m4a, mp4, wav and opus. Unsupported mediaType: {mediaType}")
        };
    }
}

