using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var timestampGranularities = ReadMetadataValue<string[]>(metadata, "timestamp_granularities");

        var bytes = Convert.FromBase64String(request.Audio.ToString()!);
        var fileName = "audio" + request.MediaType.GetAudioExtension();

        using var form = new MultipartFormDataContent();

        AddAdditionalMetadataFields(form, metadata);

        var file = new ByteArrayContent(bytes);
        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        var language = ReadMetadataValue<string>(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var prompt = ReadMetadataValue<string>(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        var temperature = ReadMetadataValue<float?>(metadata, "temperature");
        if (temperature is not null)
        {
            form.Add(
                new StringContent(temperature.Value.ToString(CultureInfo.InvariantCulture)),
                "temperature");
        }

        var wantsTimestamps = timestampGranularities is { Length: > 0 };

        var responseFormat = ReadMetadataValue<string>(metadata, "response_format");
        if (wantsTimestamps && !string.Equals(responseFormat, "verbose_json", StringComparison.OrdinalIgnoreCase))
            responseFormat = "verbose_json";

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        if (wantsTimestamps)
        {
            foreach (var g in timestampGranularities!)
                form.Add(new StringContent(g), "timestamp_granularities[]");
        }

        using var resp = await _client.PostAsync("ai/audio/transcriptions", form, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telnyx STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, request.Model);
    }

    private static void AddAdditionalMetadataFields(MultipartFormDataContent form, JsonElement metadata)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return;

        EnsureMetadataObject(metadata);

        foreach (var property in metadata.EnumerateObject())
        {
            if (property.Name.Equals("file", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("model", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("language", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("prompt", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("temperature", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("response_format", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString() ?? string.Empty
                : property.Value.GetRawText();
            form.Add(new StringContent(value), property.Name);
        }
    }

    private static T? ReadMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind == JsonValueKind.Undefined)
            return default;

        EnsureMetadataObject(metadata);

        var found = false;
        var value = default(JsonElement);
        foreach (var property in metadata.EnumerateObject())
        {
            if (!property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            found = true;
            value = property.Value;
        }

        return !found || value.ValueKind == JsonValueKind.Null
            ? default
            : value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    private static void EnsureMetadataObject(JsonElement metadata)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("Telnyx provider metadata must be a JSON object.");
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var segmentsEl = root.TryGetProperty("segments", out var s) ? s : default;
        var segments = segmentsEl.ValueKind == JsonValueKind.Array
            ? segmentsEl.EnumerateArray()
                .Select(seg => new TranscriptionSegment
                {
                    Text = seg.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "",
                    StartSecond = seg.TryGetProperty("start", out var startEl) ? (float)startEl.GetDouble() : 0,
                    EndSecond = seg.TryGetProperty("end", out var endEl) ? (float)endEl.GetDouble() : 0,
                })
                .ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),
            Segments = segments,
            Language = root.TryGetProperty("language", out var lang) ? lang.GetString() : null,
            DurationInSeconds = root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number
                ? (float)dur.GetDouble()
                : null,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = json
            }
        };
    }
}
