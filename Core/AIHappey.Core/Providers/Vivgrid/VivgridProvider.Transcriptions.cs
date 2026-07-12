using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vivgrid;

public partial class VivgridProvider
{
    private async Task<TranscriptionResponse> TranscriptionRequestVivgrid(
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
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", "audio" + request.MediaType.GetAudioExtension());
        form.Add(new StringContent(request.Model), "model");
        MergeVivgridProviderOptions(form, request.ProviderOptions, GetIdentifier(), new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "file",
            "model"
        });

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Vivgrid transcription failed ({(int)response.StatusCode}): {raw}");

        return ConvertVivgridTranscriptionResponse(raw, request.Model, now, response.GetHeaders());
    }

    private TranscriptionResponse ConvertVivgridTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IDictionary<string, string> headers)
    {
        if (!TryParseVivgridJson(raw, out var root))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Segments = [],
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(new { text = raw }),
                Response = new()
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model.ToModelId(GetIdentifier()),
                    Body = raw
                }
            };
        }

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("segments", out var segmentsElement) && segmentsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsElement.EnumerateArray())
            {
                var start = TryReadVivgridFloat(segment, "start", "start_second", "startSecond");
                var end = TryReadVivgridFloat(segment, "end", "end_second", "endSecond");

                segments.Add(new TranscriptionSegment
                {
                    Text = segment.TryGetString("text") ?? string.Empty,
                    StartSecond = start,
                    EndSecond = end < start ? start : end
                });
            }
        }

        var text = root.TryGetString("text") ?? string.Join(" ", segments.Select(segment => segment.Text));
        float? duration = null;

        if (root.TryGetProperty("duration", out var durationElement) && durationElement.ValueKind == JsonValueKind.Number)
            duration = (float)durationElement.GetDouble();

        return new TranscriptionResponse
        {
            Text = text,
            Language = root.TryGetString("language"),
            DurationInSeconds = duration,
            Segments = segments,
            ProviderMetadata = BuildVivgridProviderMetadata(root),
            Response = new()
            {
                Timestamp = timestamp,
                Headers = headers,
                ModelId = root.TryGetString("model")?.ToModelId(GetIdentifier())
                    ?? model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static bool TryParseVivgridJson(string raw, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static float TryReadVivgridFloat(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }
}
