using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.RelaxAI;

public partial class RelaxAIProvider
{
    private const string RelaxAiTranscriptionEndpoint = "v1/audio/transactions";



    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
    
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
        CancellationToken cancellationToken)
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
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Object))
            throw new JsonException("RelaxAI transcription provider metadata must be an object.");

        var now = DateTime.UtcNow;

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        // Product decision: force verbose_json by default for richer response parsing.
        var metadataResponseFormat = ReadMetadataString(metadata, "response_format");
        var responseFormat = string.IsNullOrWhiteSpace(metadataResponseFormat)
            ? "verbose_json"
            : metadataResponseFormat;

        form.Add(new StringContent(responseFormat), "response_format");

        AddMetadataString(form, metadata, "language");
        AddMetadataString(form, metadata, "prompt");

        var temperature = ReadMetadataFloat(metadata, "temperature");
        if (temperature is not null)
            form.Add(new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        AddMetadataStringArray(form, metadata, "timestamp_granularities");
        AddMetadataStringArray(form, metadata, "known_speaker_names");
        AddMetadataStringArray(form, metadata, "known_speaker_references");

        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (!IsMappedMetadataProperty(property.Name))
                    form.Add(new StringContent(GetMultipartValue(property.Value)), property.Name);
            }
        }

        using var resp = await _client.PostAsync(RelaxAiTranscriptionEndpoint, form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"RelaxAI STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model, now, resp.GetHeaders());
    }

    private static string? ReadMetadataString(JsonElement metadata, string name)
        => TryGetMetadataProperty(metadata, name, out var value)
            ? value.Deserialize<string>(JsonSerializerOptions.Web)
            : null;

    private static float? ReadMetadataFloat(JsonElement metadata, string name)
        => TryGetMetadataProperty(metadata, name, out var value)
            ? value.Deserialize<float?>(JsonSerializerOptions.Web)
            : null;

    private static void AddMetadataString(MultipartFormDataContent form, JsonElement metadata, string name)
    {
        var value = ReadMetadataString(metadata, name);
        if (!string.IsNullOrWhiteSpace(value))
            form.Add(new StringContent(value), name);
    }

    private static void AddMetadataStringArray(MultipartFormDataContent form, JsonElement metadata, string name)
    {
        if (!TryGetMetadataProperty(metadata, name, out var values))
            return;

        foreach (var value in values.Deserialize<string[]?>(JsonSerializerOptions.Web) ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
                form.Add(new StringContent(value), name);
        }
    }

    private static bool TryGetMetadataProperty(JsonElement metadata, string name, out JsonElement value)
    {
        value = default;
        var found = false;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    found = true;
                }
            }
        }

        return found;
    }

    private static bool IsMappedMetadataProperty(string name)
        => name.Equals("known_speaker_names", StringComparison.OrdinalIgnoreCase)
            || name.Equals("known_speaker_references", StringComparison.OrdinalIgnoreCase)
            || name.Equals("language", StringComparison.OrdinalIgnoreCase)
            || name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
            || name.Equals("response_format", StringComparison.OrdinalIgnoreCase)
            || name.Equals("temperature", StringComparison.OrdinalIgnoreCase)
            || name.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase)
            || name.Equals("file", StringComparison.OrdinalIgnoreCase)
            || name.Equals("model", StringComparison.OrdinalIgnoreCase);

    private static string GetMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();

    private static TranscriptionResponse ConvertTranscriptionResponse(
            string json, string model, DateTime timestamp,
            Dictionary<string, string> headers)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segments = new List<TranscriptionSegment>();

        if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var segment in segmentsEl.EnumerateArray())
            {
                var segmentText = segment.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                    ? textEl.GetString() ?? string.Empty
                    : string.Empty;

                var start = TryReadFloat(segment, "start", "start_second", "startSecond");
                var end = TryReadFloat(segment, "end", "end_second", "endSecond");

                if (end < start)
                    end = start;

                segments.Add(new TranscriptionSegment
                {
                    Text = segmentText,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? string.Empty
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
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Response = new()
            {
                Timestamp = timestamp,
                Headers = headers,
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
