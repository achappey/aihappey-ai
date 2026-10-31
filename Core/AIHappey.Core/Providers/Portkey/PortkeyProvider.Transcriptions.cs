using AIHappey.Core.AI;
using System.Globalization;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Portkey;

public partial class PortkeyProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var audioString = request.Audio?.ToString();
        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        var bytes = Convert.FromBase64String(audioString);
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var language = ReadOption<string>(metadata, "language");
        var prompt = ReadOption<string>(metadata, "prompt");
        var responseFormatOption = ReadOption<string>(metadata, "response_format");
        var temperature = ReadOption<float?>(metadata, "temperature");
        var timestampGranularities = ReadOption<IEnumerable<string>>(metadata, "timestamp_granularities");

        using var form = new MultipartFormDataContent();

        var fileName = "audio" + request.MediaType.GetAudioExtension();
        form.Add(new ByteArrayContent(bytes), "file", fileName);

        form.Add(new StringContent(request.Model), "model");

        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        var responseFormat = string.IsNullOrWhiteSpace(responseFormatOption)
            ? null
            : responseFormatOption.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        if (temperature is not null)
        {
            form.Add(
                new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)),
                "temperature");
        }

        if (timestampGranularities is not null)
        {
            foreach (var granularity in timestampGranularities.Where(g => !string.IsNullOrWhiteSpace(g)))
                form.Add(new StringContent(granularity), "timestamp_granularities[]");
        }

        AddRawOptions(form, metadata,
            "file", "model", "language", "prompt", "response_format", "temperature", "timestamp_granularities");

        using var resp = await _client.PostAsync("v1/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Portkey STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model);
    }

    private static T? ReadOption<T>(JsonElement? options, string name)
    {
        if (options is not { ValueKind: JsonValueKind.Object } metadata)
            return default;

        foreach (var property in metadata.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                return property.Value.Deserialize<T>(JsonSerializerOptions.Web);
        }

        return default;
    }

    private static void AddRawOptions(MultipartFormDataContent form, JsonElement? options, params string[] mappedProperties)
    {
        if (options is not { ValueKind: JsonValueKind.Object } metadata)
            return;

        foreach (var property in metadata.EnumerateObject())
        {
            if (mappedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();

            form.Add(new StringContent(value, Encoding.UTF8), property.Name);
        }
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segmentsEl = root.TryGetProperty("segments", out var s)
            ? s
            : default;

        var segments = segmentsEl.ValueKind == JsonValueKind.Array
            ? segmentsEl.EnumerateArray()
                .Select(seg => new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl)
                        ? textEl.GetString() ?? ""
                        : "",
                    StartSecond = seg.TryGetProperty("start", out var startEl) && startEl.TryGetDouble(out var start)
                        ? (float)start
                        : 0f,
                    EndSecond = seg.TryGetProperty("end", out var endEl) && endEl.TryGetDouble(out var end)
                        ? (float)end
                        : 0f
                })
                .ToList()
            : [];

        float? durationInSeconds = null;
        if (root.TryGetProperty("duration", out var durationEl))
        {
            if (durationEl.ValueKind == JsonValueKind.Number && durationEl.TryGetDouble(out var durationNum))
                durationInSeconds = (float)durationNum;
            else if (durationEl.ValueKind == JsonValueKind.String
                && float.TryParse(durationEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var durationStr))
                durationInSeconds = durationStr;
        }

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),

            Segments = segments,

            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null,

            DurationInSeconds = durationInSeconds,

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }


}
