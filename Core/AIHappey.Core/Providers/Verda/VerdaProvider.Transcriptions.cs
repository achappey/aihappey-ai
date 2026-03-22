using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Verda;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Verda;

public partial class VerdaProvider
{
    private async Task<TranscriptionResponse> VerdaTranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var now = DateTime.UtcNow;
        List<object> warnings = [];

        var metadata = request.GetProviderMetadata<VerdaTranscriptionProviderMetadata>(GetIdentifier());

        var payload = new Dictionary<string, object?>
        {
            ["audio_input"] = ResolveAudioInput(request, metadata)
        };

        if (metadata?.Translate is not null)
            payload["translate"] = metadata.Translate.Value;

        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            payload["language"] = metadata.Language!.Trim();

        if (!string.IsNullOrWhiteSpace(metadata?.ProcessingType))
            payload["processing_type"] = metadata.ProcessingType!.Trim();

        if (!string.IsNullOrWhiteSpace(metadata?.Output))
            payload["output"] = metadata.Output!.Trim();

        if (!string.Equals(request.Model, "whisper", StringComparison.OrdinalIgnoreCase)
            && !request.Model.EndsWith("/whisper", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new
            {
                type = "info",
                feature = "model",
                details = "Verda Whisper endpoint uses a fixed model; request.model is preserved in response metadata only."
            });
        }

        var json = JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);

        using var resp = await _client.PostAsync(
            "whisper/predict",
            new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json),
            cancellationToken);

        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Verda transcription request failed ({(int)resp.StatusCode}): {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var isSubtitles = string.Equals(metadata?.Output, "subtitles", StringComparison.OrdinalIgnoreCase);

        var text = isSubtitles
            ? ReadSubtitleText(root)
            : ReadText(root);

        var segments = isSubtitles
            ? [.. Array.Empty<TranscriptionSegment>()]
            : ParseSegments(root);

        var duration = ReadDuration(root);
        if (!duration.HasValue && segments.Count > 0)
            duration = segments.Max(s => s.EndSecond);

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            [GetIdentifier()] = JsonSerializer.SerializeToElement(new
            {
                endpoint = "whisper/predict",
                output = metadata?.Output,
                processing_type = metadata?.ProcessingType,
                translate = metadata?.Translate,
                request = payload
            }, JsonSerializerOptions.Web)
        };

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadString(root, "language"),
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = providerMetadata,
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static void MergeRawMetadataPassthrough(
        Dictionary<string, object?> payload,
        Dictionary<string, JsonElement>? additionalProperties)
    {
        if (additionalProperties is null || additionalProperties.Count == 0)
            return;

        foreach (var property in additionalProperties)
        {
            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                continue;

            payload[property.Key] = property.Value.Clone();
        }
    }

    private static string ResolveAudioInput(TranscriptionRequest request, VerdaTranscriptionProviderMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.AudioInput))
            return metadata.AudioInput!.Trim();

        var audio = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audio))
            throw new ArgumentException("Audio is required.", nameof(request));

        audio = audio.Trim();

        if (MediaContentHelpers.TryParseDataUrl(audio, out _, out _))
            return audio;

        if (Uri.TryCreate(audio, UriKind.Absolute, out var uri) && (uri.Scheme is "http" or "https"))
            return audio;

        return LooksLikeBase64(audio)
            ? $"data:{request.MediaType};base64,{audio}"
            : audio;
    }

    private static bool LooksLikeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            _ = Convert.FromBase64String(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReadSubtitleText(JsonElement root)
        => ReadString(root, "output")
            ?? ReadString(root, "subtitles")
            ?? ReadString(root, "text")
            ?? string.Empty;

    private static string ReadText(JsonElement root)
    {
        var direct = ReadString(root, "text");
        if (!string.IsNullOrWhiteSpace(direct))
            return direct;

        var segments = ParseSegments(root);
        if (segments.Count > 0)
            return string.Join(" ", segments.Select(a => a.Text));

        return string.Empty;
    }

    private static List<TranscriptionSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return segments;

        foreach (var segment in segmentsEl.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object)
                continue;

            var text = ReadString(segment, "text") ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var start = ReadNumber(segment, "start")
                ?? ReadNumber(segment, "start_second")
                ?? ReadNumber(segment, "startSecond")
                ?? 0f;

            var end = ReadNumber(segment, "end")
                ?? ReadNumber(segment, "end_second")
                ?? ReadNumber(segment, "endSecond")
                ?? start;

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

    private static float? ReadDuration(JsonElement root)
    {
        var topLevelDuration = ReadNumber(root, "duration");
        if (topLevelDuration.HasValue)
            return topLevelDuration;

        if (root.TryGetProperty("usage", out var usage)
            && usage.ValueKind == JsonValueKind.Object)
        {
            var usageDuration = ReadNumber(usage, "audio_duration_seconds");
            if (usageDuration.HasValue)
                return usageDuration;
        }

        return null;
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return value.GetString();
    }

    private static float? ReadNumber(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number)
            return null;

        return (float)value.GetDouble();
    }
}

