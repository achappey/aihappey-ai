using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Core.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.APIpie;

public partial class APIpieProvider
{
    private const string ApiPieTranscriptionEndpoint = "v1/audio/transcriptions";


    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionRequestAsync(
            options,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleTranscriptionStreamingAsync(
            options,
            cancellationToken: cancellationToken);
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
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        var bytes = Convert.FromBase64String(audioString);
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        EnsureMetadataObject(metadata);
        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        AddAdditionalMetadataFields(form, metadata);

        var prompt = ReadOptionalString(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        var language = ReadOptionalString(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var temperature = ReadOptionalFloat(metadata, "temperature");
        if (temperature is not null)
            form.Add(new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        var timestampGranularities = ReadOptionalStringArray(metadata, "timestampGranularities");
        var wantsTimestamps = timestampGranularities?.Any() == true;

        var responseFormat = ReadOptionalString(metadata, "responseFormat");
        if (wantsTimestamps && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        if (wantsTimestamps)
        {
            foreach (var granularity in timestampGranularities!.Where(g => !string.IsNullOrWhiteSpace(g)))
                form.Add(new StringContent(granularity!), "timestamp_granularities");
        }

        using var resp = await _client.PostAsync(ApiPieTranscriptionEndpoint, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"APIpie STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now);
    }

    private static void AddAdditionalMetadataFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("language", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("temperature", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("responseFormat", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("timestampGranularities", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static float? ReadOptionalFloat(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        return value.Deserialize<float?>(JsonSerializerOptions.Web);
    }

    private static string?[]? ReadOptionalStringArray(JsonElement metadata, string name)
    {
        var value = FindProperty(metadata, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return null;

        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonException($"APIpie provider metadata '{name}' must be an array.");

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.Null
                ? null
                : item.Deserialize<string>(JsonSerializerOptions.Web))
            .ToArray();
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model, DateTime timestamp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var text = segment.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                var start = TryReadFloat(segment, "start", "start_second", "startSecond");
                var end = TryReadFloat(segment, "end", "end_second", "endSecond");

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
            : string.Join(" ", segments.Select(a => a.Text));

        var language = root.TryGetProperty("language", out var languageEl) && languageEl.ValueKind == JsonValueKind.String
            ? languageEl.GetString()
            : null;

        float? duration = null;

        if (root.TryGetProperty("duration", out var durationEl) && durationEl.ValueKind == JsonValueKind.Number)
            duration = (float)durationEl.GetDouble();

        if (!duration.HasValue && root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
        {
            if (usageEl.TryGetProperty("seconds", out var secondsEl) && secondsEl.ValueKind == JsonValueKind.Number)
                duration = (float)secondsEl.GetDouble();
        }

        return new TranscriptionResponse
        {
            Text = textValue,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                ModelId = root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String
                    ? modelEl.GetString() ?? model
                    : model,
                Body = json
            }
        };
    }

    private static float TryReadFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }
}
