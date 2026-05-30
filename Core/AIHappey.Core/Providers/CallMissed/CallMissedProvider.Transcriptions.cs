using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIHappey.Core.Providers.CallMissed;

public partial class CallMissedProvider
{
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
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
            {
                if (string.Equals(property.Name, "file", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(property.Name, "model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryConvertFormScalar(property.Value, out var value))
                    form.Add(new StringContent(value), property.Name);
            }
        }

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"CallMissed STT failed ({(int)resp.StatusCode}): {raw}");

        return ConvertTranscriptionResponse(raw, request.Model, now, providerOptions);
    }

    private static bool TryConvertFormScalar(JsonElement value, out string scalar)
    {
        scalar = string.Empty;

        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                scalar = value.GetString() ?? string.Empty;
                return true;
            case JsonValueKind.Number:
                scalar = value.GetRawText();
                return true;
            case JsonValueKind.True:
                scalar = "true";
                return true;
            case JsonValueKind.False:
                scalar = "false";
                return true;
            default:
                return false;
        }
    }

    private TranscriptionResponse ConvertTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        JsonElement providerOptions)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var text = ReadTranscriptionString(segment, "text") ?? string.Empty;
                var start = ReadTranscriptionFloat(segment, "start") ?? ReadTranscriptionFloat(segment, "start_second") ?? 0f;
                var end = ReadTranscriptionFloat(segment, "end") ?? ReadTranscriptionFloat(segment, "end_second") ?? start;

                if (end < start)
                    end = start;

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        var textValue = ReadTranscriptionString(root, "text") ?? string.Join(" ", segments.Select(a => a.Text));
        var language = ReadTranscriptionString(root, "language");

        float? duration = ReadTranscriptionFloat(root, "duration");
        if (!duration.HasValue && root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            duration = ReadTranscriptionFloat(usageEl, "seconds");

        return new TranscriptionResponse
        {
            Text = textValue,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    endpoint = "v1/audio/transcriptions",
                    request = providerOptions.ValueKind == JsonValueKind.Object
                        ? JsonSerializer.Deserialize<object>(providerOptions.GetRawText(), JsonSerializerOptions.Web)
                        : null
                })
            },
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = ReadTranscriptionString(root, "model") ?? model,
                Body = raw
            }
        };
    }

    private static string? ReadTranscriptionString(JsonElement el, string propertyName)
    {
        return el.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;
    }

    private static float? ReadTranscriptionFloat(JsonElement el, string propertyName)
    {
        if (!el.TryGetProperty(propertyName, out var prop))
            return null;

        if (prop.ValueKind == JsonValueKind.Number)
            return (float)prop.GetDouble();

        if (prop.ValueKind == JsonValueKind.String && float.TryParse(prop.GetString(), out var parsed))
            return parsed;

        return null;
    }
}
