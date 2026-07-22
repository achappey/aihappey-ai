using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Infercom;

public partial class InfercomProvider
{
    private static readonly HashSet<string> ReservedInfercomTranscriptionFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "file", "model", "stream"
    };


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


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
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
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

        AddInfercomProviderOptions(form, request.ProviderOptions);

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Infercom transcription failed ({(int)response.StatusCode}): {raw}");

        return ConvertInfercomTranscriptionResponse(raw, request.Model, now, response.GetHeaders());
    }

    private static void AddInfercomProviderOptions(
        MultipartFormDataContent form,
        Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions is null)
            return;

        if (!providerOptions.TryGetValue(nameof(Infercom).ToLowerInvariant(), out var options))
            return;

        if (options.ValueKind != JsonValueKind.Object)
            return;

        foreach (var property in options.EnumerateObject())
        {
            if (ReservedInfercomTranscriptionFields.Contains(property.Name))
                continue;

            AddInfercomFormField(form, property.Name, property.Value);
        }
    }

    private static void AddInfercomFormField(
        MultipartFormDataContent form,
        string name,
        JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Undefined:
            case JsonValueKind.Null:
                return;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                    AddInfercomFormField(form, name, item);
                return;
            case JsonValueKind.String:
                form.Add(new StringContent(value.GetString() ?? string.Empty), name);
                return;
            case JsonValueKind.Number:
                form.Add(new StringContent(value.GetRawText()), name);
                return;
            case JsonValueKind.True:
                form.Add(new StringContent(bool.TrueString.ToLowerInvariant()), name);
                return;
            case JsonValueKind.False:
                form.Add(new StringContent(bool.FalseString.ToLowerInvariant()), name);
                return;
            default:
                form.Add(new StringContent(value.GetRawText()), name);
                return;
        }
    }

    private static TranscriptionResponse ConvertInfercomTranscriptionResponse(
        string raw,
        string model,
        DateTime timestamp,
        IDictionary<string, string> headers)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new TranscriptionResponse
            {
                Text = string.Empty,
                Segments = [],
                ProviderMetadata = nameof(Infercom).ToLowerInvariant().CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model,
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
                ProviderMetadata = nameof(Infercom).ToLowerInvariant().CreatePrimitiveProviderMetadata(new { text = raw }),
                Response = new ResponseData
                {
                    Timestamp = timestamp,
                    Headers = headers,
                    ModelId = model,
                    Body = raw
                }
            };
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement.Clone();
        var segments = ExtractInfercomTranscriptionSegments(root);

        var text = ReadInfercomString(root, "text") ?? string.Join(" ", segments.Select(static segment => segment.Text));
        var duration = ReadInfercomFloat(root, "duration");

        if (!duration.HasValue
            && root.TryGetProperty("usage", out var usageEl)
            && usageEl.ValueKind == JsonValueKind.Object)
        {
            duration = ReadInfercomFloat(usageEl, "seconds");
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = ReadInfercomString(root, "language"),
            DurationInSeconds = duration,
            Segments = segments,
            ProviderMetadata = nameof(Infercom).ToLowerInvariant().CreatePrimitiveProviderMetadata(root),
            Response = new ResponseData
            {
                Timestamp = timestamp,
                Headers = headers,
                ModelId = ReadInfercomString(root, "model") ?? model,
                Body = root
            }
        };
    }

    private static List<TranscriptionSegment> ExtractInfercomTranscriptionSegments(JsonElement root)
    {
        if (!root.TryGetProperty("segments", out var segmentsEl) || segmentsEl.ValueKind != JsonValueKind.Array)
            return [];

        return segmentsEl
            .EnumerateArray()
            .Where(static segment => segment.ValueKind == JsonValueKind.Object)
            .Select(static segment => new TranscriptionSegment
            {
                Text = ReadInfercomString(segment, "text") ?? string.Empty,
                StartSecond = ReadInfercomFloat(segment, "start", "start_second", "startSecond") ?? 0f,
                EndSecond = ReadInfercomFloat(segment, "end", "end_second", "endSecond") ?? 0f
            })
            .ToList();
    }

    private static string? ReadInfercomString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }

        return null;
    }

    private static float? ReadInfercomFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();

            if (element.TryGetProperty(name, out value)
                && value.ValueKind == JsonValueKind.String
                && float.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

}
