using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIHappey.Core.Providers.Requesty;

public partial class RequestyProvider
{
    private async Task<TranscriptionResponse> RequestyTranscriptionRequest(
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

        var language = TryGetRequestyProviderString(request.ProviderOptions, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var prompt = TryGetRequestyProviderString(request.ProviderOptions, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        var temperature = TryGetRequestyProviderNumber(request.ProviderOptions, "temperature");
        if (temperature is not null)
            form.Add(new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)), "temperature");

        var responseFormat = TryGetRequestyProviderString(request.ProviderOptions, "response_format", "responseFormat");
        var timestampGranularities = TryGetRequestyProviderStringArray(request.ProviderOptions, "timestamp_granularities", "timestampGranularities");
        if (timestampGranularities.Count > 0 && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        foreach (var granularity in timestampGranularities)
            form.Add(new StringContent(granularity), "timestamp_granularities[]");

        using var response = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Requesty STT failed ({(int)response.StatusCode}): {json}");

        return ConvertRequestyTranscriptionResponse(json, request.Model, now);
    }

    private static TranscriptionResponse ConvertRequestyTranscriptionResponse(string json, string model, DateTime timestamp)
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

                var start = TryReadRequestyFloat(segment, "start", "start_second", "startSecond");
                var end = TryReadRequestyFloat(segment, "end", "end_second", "endSecond");

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

    private static float TryReadRequestyFloat(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number)
                return (float)value.GetDouble();
        }

        return 0f;
    }

    private static string? TryGetRequestyProviderString(Dictionary<string, JsonElement>? providerOptions, params string[] keys)
    {
        if (!TryGetRequestyProviderOptions(providerOptions, out var options))
            return null;

        foreach (var key in keys)
        {
            if (!options.TryGetProperty(key, out var el) || el.ValueKind != JsonValueKind.String)
                continue;

            var value = el.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static double? TryGetRequestyProviderNumber(Dictionary<string, JsonElement>? providerOptions, params string[] keys)
    {
        if (!TryGetRequestyProviderOptions(providerOptions, out var options))
            return null;

        foreach (var key in keys)
        {
            if (options.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.Number)
                return el.GetDouble();
        }

        return null;
    }

    private static List<string> TryGetRequestyProviderStringArray(Dictionary<string, JsonElement>? providerOptions, params string[] keys)
    {
        if (!TryGetRequestyProviderOptions(providerOptions, out var options))
            return [];

        foreach (var key in keys)
        {
            if (!options.TryGetProperty(key, out var el))
                continue;

            if (el.ValueKind == JsonValueKind.String)
            {
                var value = el.GetString();
                return string.IsNullOrWhiteSpace(value) ? [] : [value];
            }

            if (el.ValueKind == JsonValueKind.Array)
            {
                return el.EnumerateArray()
                    .Where(static item => item.ValueKind == JsonValueKind.String)
                    .Select(static item => item.GetString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value!)
                    .ToList();
            }
        }

        return [];
    }

    private static bool TryGetRequestyProviderOptions(Dictionary<string, JsonElement>? providerOptions, out JsonElement options)
    {
        options = default;

        if (providerOptions is null)
            return false;

        if (!providerOptions.TryGetValue("requesty", out options))
            return false;

        return options.ValueKind == JsonValueKind.Object;
    }
}
