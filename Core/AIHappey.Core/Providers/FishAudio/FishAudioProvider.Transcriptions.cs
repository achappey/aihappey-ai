using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken)
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
        var warnings = new List<object>();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var language = TryGetString(metadata, "language");
        var ignoreTimestamps = TryGetBoolean(metadata, "ignore_timestamps");

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Audio must be base64 or a data-url containing base64.", ex);
        }

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var audioContent = new ByteArrayContent(bytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);
        form.Add(audioContent, "audio", fileName);

        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language, Encoding.UTF8), "language");

        if (ignoreTimestamps is not null)
            form.Add(new StringContent(ignoreTimestamps.Value.ToString().ToLowerInvariant(), Encoding.UTF8), "ignore_timestamps");

        using var resp = await _client.PostAsync("v1/asr", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"FishAudio STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now, warnings);
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        DateTime now,
        IEnumerable<object> warnings)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? (textEl.GetString() ?? string.Empty)
            : string.Empty;

        float? duration = null;
        if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            duration = (float)durEl.GetDouble();

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segEl) && segEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in segEl.EnumerateArray())
            {
                var segText = item.TryGetProperty("text", out var stEl) && stEl.ValueKind == JsonValueKind.String
                    ? (stEl.GetString() ?? string.Empty)
                    : string.Empty;

                var start = item.TryGetProperty("start", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? (float)sEl.GetDouble()
                    : 0f;

                var end = item.TryGetProperty("end", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                    ? (float)eEl.GetDouble()
                    : 0f;

                if (!string.IsNullOrWhiteSpace(segText))
                {
                    segments.Add(new TranscriptionSegment
                    {
                        Text = segText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            Response = new()
            {
                Timestamp = now,
                ModelId = model,
                Body = json,
            }
        };
    }

    private static string? TryGetString(JsonElement metadata, string key)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(key, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        return prop.GetString();
    }

    private static bool? TryGetBoolean(JsonElement metadata, string key)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return null;

        if (!metadata.TryGetProperty(key, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.True || prop.ValueKind == JsonValueKind.False)
            return prop.GetBoolean();

        return null;
    }
}

