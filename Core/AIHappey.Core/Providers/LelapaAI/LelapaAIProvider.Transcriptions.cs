using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.LelapaAI;

public partial class LelapaAIProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.GetProviderMetadata<LelapaAITranscriptionProviderMetadata>(GetIdentifier());
        var query = BuildTranscriptionQuery(metadata);
        var endpoint = string.IsNullOrWhiteSpace(query)
            ? "v1/transcribe/sync"
            : $"v1/transcribe/sync?{query}";

        var audioString = request.Audio?.ToString()
            ?? throw new ArgumentException("Audio is required.", nameof(request));
        var audioBytes = Convert.FromBase64String(audioString.RemoveDataUrlPrefix());
        var mediaType = string.IsNullOrWhiteSpace(request.MediaType)
            ? "audio/wav"
            : request.MediaType;
        var fileName = "audio" + mediaType.GetAudioExtension();

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(audioBytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        form.Add(file, "file", fileName);

        using var response = await _client.PostAsync(endpoint, form, cancellationToken);
        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"LelapaAI transcription failed ({(int)response.StatusCode}): {rawBody}");

        using var doc = JsonDocument.Parse(rawBody);
        return ConvertTranscriptionResponse(
            doc.RootElement,
            request.Model,
            rawBody,
            response.GetHeaders());
    }

    private static string BuildTranscriptionQuery(LelapaAITranscriptionProviderMetadata? metadata)
    {
        var query = new List<string>();

        if (!string.IsNullOrWhiteSpace(metadata?.LangCode))
        {
            if (!IsSupportedTranscriptionLanguage(metadata.LangCode))
                throw new ArgumentException($"Unsupported LelapaAI transcription language '{metadata.LangCode}'.", nameof(metadata));

            query.Add($"lang_code={Uri.EscapeDataString(metadata.LangCode)}");
        }

        if (metadata?.Diarise is not null)
            query.Add($"diarise={(metadata.Diarise.Value ? "1" : "0")}");

        if (metadata?.Diarize is not null)
            query.Add($"diarise={(metadata.Diarize.Value ? "1" : "0")}");

        if (metadata?.DetectMusic is not null)
            query.Add($"detect_music={(metadata.DetectMusic.Value ? "1" : "0")}");

        return string.Join('&', query);
    }

    private TranscriptionResponse ConvertTranscriptionResponse(
        JsonElement root,
        string model,
        string rawBody,
        IDictionary<string, string>? headers)
    {
        var text = TryGetJsonString(root, "transcription_text") ?? string.Empty;
        var language = TryGetJsonString(root, "language_code");
        var duration = TryGetJsonFloat(root, "audio_length_seconds");
        var warnings = TryGetWarnings(root);

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = ExtractTranscriptionSegments(root),
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Response = new ResponseData
            {
                ModelId = model,
                Timestamp = DateTime.UtcNow,
                Body = rawBody,
                Headers = headers
            }
        };
    }

    private static IEnumerable<TranscriptionSegment> ExtractTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("diarisation_result", out var diarisation)
            || diarisation.ValueKind != JsonValueKind.Object
            || !diarisation.TryGetProperty("timeline", out var timeline)
            || timeline.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var segments = new List<TranscriptionSegment>();
        foreach (var item in timeline.EnumerateArray())
        {
            var segmentText = TryGetJsonString(item, "text");
            if (string.IsNullOrWhiteSpace(segmentText))
                continue;

            segments.Add(new TranscriptionSegment
            {
                Text = segmentText,
                StartSecond = TryGetJsonFloat(item, "start_time") ?? 0,
                EndSecond = TryGetJsonFloat(item, "end_time") ?? 0
            });
        }

        return segments;
    }

    private static IEnumerable<object> TryGetWarnings(JsonElement root)
    {
        if (!root.TryGetProperty("warnings", out var warnings)
            || warnings.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return [];
        }

        return [warnings.Clone()];
    }

    private static string? TryGetJsonString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();
        }

        return null;
    }

    private static float? TryGetJsonFloat(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                continue;

            return property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetSingle(out var value)
                ? value
                : null;
        }

        return null;
    }

    private sealed class LelapaAITranscriptionProviderMetadata
    {
        public string? LangCode { get; set; }

        public bool? Diarise { get; set; }

        public bool? Diarize { get; set; }

        public bool? DetectMusic { get; set; }
    }


}
