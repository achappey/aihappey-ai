using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.TheRouterAI;

public partial class TheRouterAIProvider
{
    private const string TheRouterAITranscriptionEndpoint = "v1/audio/transcriptions";

    private async Task<TranscriptionResponse> TheRouterAITranscriptionRequest(
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
            JsonElement { ValueKind: JsonValueKind.String } json => json.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var providerOptions = TheRouterAIProviderOptionsToDictionary(request.ProviderOptions);
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        foreach (var option in providerOptions)
        {
            if (string.Equals(option.Key, "file", StringComparison.Ordinal)
                || string.Equals(option.Key, "model", StringComparison.Ordinal))
            {
                continue;
            }

            AddTheRouterAIFormValue(form, option.Key, option.Value);
        }

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        using var resp = await _client.PostAsync(TheRouterAITranscriptionEndpoint, form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"TheRouterAI STT failed ({(int)resp.StatusCode}): {raw}");

        return ConvertTheRouterAITranscriptionResponse(
            raw,
            request.Model,
            now,
            resp.GetHeaders(),
            providerOptions);
    }

    private static void AddTheRouterAIFormValue(
        MultipartFormDataContent form,
        string name,
        object? value)
    {
        switch (value)
        {
            case null:
                return;
            case JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined }:
                return;
            case JsonElement { ValueKind: JsonValueKind.Array } array:
                foreach (var item in array.EnumerateArray())
                    AddTheRouterAIFormValue(form, name, item.Clone());
                return;
            case JsonElement json:
                form.Add(new StringContent(JsonElementToFormString(json)), name);
                return;
            case IEnumerable<string> values when value is not string:
                foreach (var item in values.Where(static item => !string.IsNullOrWhiteSpace(item)))
                    form.Add(new StringContent(item), name);
                return;
            case IFormattable formattable:
                form.Add(new StringContent(formattable.ToString(null, CultureInfo.InvariantCulture)), name);
                return;
            default:
                form.Add(new StringContent(value.ToString() ?? string.Empty), name);
                return;
        }
    }

    private static string JsonElementToFormString(JsonElement json)
        => json.ValueKind switch
        {
            JsonValueKind.String => json.GetString() ?? string.Empty,
            JsonValueKind.Number => json.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => json.GetRawText()
        };

    private TranscriptionResponse ConvertTheRouterAITranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IDictionary<string, string>? headers,
        IReadOnlyDictionary<string, object?> requestFields)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var segments = new List<TranscriptionSegment>();

            if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var segment in segmentsEl.EnumerateArray())
                {
                    var text = segment.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                        ? textEl.GetString() ?? string.Empty
                        : string.Empty;

                    var start = TryReadTheRouterAIFloat(segment, "start", "start_second", "startSecond");
                    var end = TryReadTheRouterAIFloat(segment, "end", "end_second", "endSecond");

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

            var textValue = root.TryGetProperty("text", out var textRootEl) && textRootEl.ValueKind == JsonValueKind.String
                ? textRootEl.GetString() ?? string.Empty
                : string.Join(" ", segments.Select(static segment => segment.Text));

            var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
                ? languageEl.GetString()
                : null;

            float? duration = null;
            if (root.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
                duration = (float)durationEl.GetDouble();

            return new TranscriptionResponse
            {
                Text = textValue,
                Language = language,
                DurationInSeconds = duration,
                Segments = segments,
                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                        ? modelEl.GetString()?.ToModelId(GetIdentifier()) ?? model.ToModelId(GetIdentifier())
                        : model.ToModelId(GetIdentifier()),
                    Body = root.Clone()
                },
                Request = new TranscriptionRequestItem
                {
                    Body = JsonSerializer.Serialize(requestFields, TheRouterAIJsonOptions)
                }
            };
        }
        catch (JsonException)
        {
            return new TranscriptionResponse
            {
                Text = raw,
                Segments = [],
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
                    Body = JsonSerializer.Serialize(requestFields, TheRouterAIJsonOptions)
                }
            };
        }
    }

    private static float TryReadTheRouterAIFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }
}
