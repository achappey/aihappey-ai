using System.Net.Http.Headers;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Melious;

public partial class MeliousProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
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
        var providerOptions = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        AddMeliousProviderOptionsToForm(
            form,
            providerOptions,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "file",
                "model"
            });

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model.Trim()), "model");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Melious STT failed ({(int)response.StatusCode}): {raw}");

        return ConvertMeliousTranscriptionResponse(raw, request.Model, now, providerOptions);
    }

    private TranscriptionResponse ConvertMeliousTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        JsonElement providerOptions)
    {
        if (!TryParseMeliousTranscriptionJson(raw, out var root))
        {
            return new TranscriptionResponse
            {
                Text = raw,
                ProviderMetadata = BuildMeliousTranscriptionMetadata(providerOptions),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    ModelId = model,
                    Body = raw
                }
            };
        }

        using var document = root.Value.Document;
        var rootElement = root.Value.Element;
        var segments = new List<TranscriptionSegment>();

        if (rootElement.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var start = ReadMeliousFloatProperty(segment, "start")
                    ?? ReadMeliousFloatProperty(segment, "start_second")
                    ?? ReadMeliousFloatProperty(segment, "startSecond")
                    ?? 0f;
                var end = ReadMeliousFloatProperty(segment, "end")
                    ?? ReadMeliousFloatProperty(segment, "end_second")
                    ?? ReadMeliousFloatProperty(segment, "endSecond")
                    ?? start;

                if (end < start)
                    end = start;

                segments.Add(new TranscriptionSegment
                {
                    Text = ReadMeliousStringProperty(segment, "text") ?? string.Empty,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        var text = ReadMeliousStringProperty(rootElement, "text")
            ?? string.Join(" ", segments.Select(static segment => segment.Text));
        var language = ReadMeliousStringProperty(rootElement, "language");
        var duration = ReadMeliousFloatProperty(rootElement, "duration");

        if (!duration.HasValue
            && rootElement.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object)
        {
            duration = ReadMeliousFloatProperty(usageEl, "seconds")
                ?? ReadMeliousFloatProperty(usageEl, "duration");
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            ProviderMetadata = BuildMeliousTranscriptionMetadata(providerOptions),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                ModelId = ReadMeliousStringProperty(rootElement, "model") ?? model,
                Body = raw
            }
        };
    }

    private static Dictionary<string, JsonElement> BuildMeliousTranscriptionMetadata(JsonElement providerOptions)
        => new()
        {
            [nameof(Melious).ToLowerInvariant()] = JsonSerializer.SerializeToElement(new
            {
                endpoint = "v1/audio/transcriptions",
                request = providerOptions.ValueKind == JsonValueKind.Object
                    ? JsonSerializer.Deserialize<object>(providerOptions.GetRawText(), JsonSerializerOptions.Web)
                    : null
            }, JsonSerializerOptions.Web)
        };

    private static bool TryParseMeliousTranscriptionJson(string raw, out (JsonDocument Document, JsonElement Element)? parsed)
    {
        try
        {
            var document = JsonDocument.Parse(raw);
            parsed = (document, document.RootElement.Clone());
            return true;
        }
        catch (JsonException)
        {
            parsed = null;
            return false;
        }
    }
}
