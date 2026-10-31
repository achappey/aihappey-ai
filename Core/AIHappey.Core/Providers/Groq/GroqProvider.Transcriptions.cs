using System.Text.Json;
using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using System.Globalization;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = request.Model;

        ArgumentNullException.ThrowIfNull(request.Audio);
        var audioData = request.Audio.ToString();
        if (string.IsNullOrWhiteSpace(audioData))
            throw new ArgumentException("Audio data is required.", nameof(request));

        var bytes = Convert.FromBase64String(audioData.RemoveDataUrlPrefix());
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        var timestampGranularities = ReadMetadataValue<string[]>(metadata, "timestamp_granularities");
        using var form = new MultipartFormDataContent();

        AddAdditionalMetadataFields(form, metadata);

        // file (Groq accepts per-part Content-Type)
        var fileName = "audio" + request.MediaType.GetAudioExtension();
        var file = new ByteArrayContent(bytes);

        if (!string.IsNullOrWhiteSpace(request.MediaType))
            file.Headers.ContentType = new MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);

        // required
        form.Add(new StringContent(model), "model");

        // optional
        var language = ReadMetadataValue<string>(metadata, "language");
        if (!string.IsNullOrWhiteSpace(language))
            form.Add(new StringContent(language), "language");

        var prompt = ReadMetadataValue<string>(metadata, "prompt");
        if (!string.IsNullOrWhiteSpace(prompt))
            form.Add(new StringContent(prompt), "prompt");

        // temperature (optional)
        var temperature = ReadMetadataValue<float?>(metadata, "temperature");
        if (temperature is not null)
        {
            form.Add(
                new StringContent(
                    temperature.Value.ToString(CultureInfo.InvariantCulture)
                ),
                "temperature"
            );
        }

        var responseFormat = ReadMetadataValue<string>(metadata, "response_format");
        if (timestampGranularities is { Length: > 0 })
        {
            foreach (var g in timestampGranularities)
            {
                form.Add(new StringContent(g), "timestamp_granularities[]");
            }

            responseFormat = "verbose_json";
        }

        if (!string.IsNullOrWhiteSpace(responseFormat))
            form.Add(new StringContent(responseFormat), "response_format");

        using var resp = await _client.PostAsync(
            "v1/audio/transcriptions",
            form,
            cancellationToken
        );

        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Groq STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model.ToModelId(GetIdentifier()), resp.GetHeaders());
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
                || property.Name.Equals("timestamp_granularities", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("response_format", StringComparison.OrdinalIgnoreCase))
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
            throw new JsonException("Groq provider metadata must be a JSON object.");
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(
        string json,
        string model,
        Dictionary<string, string> headers)
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
                    Text = seg.GetProperty("text").GetString() ?? "",
                    StartSecond = (float)seg.GetProperty("start").GetDouble(),
                    EndSecond = (float)seg.GetProperty("end").GetDouble()
                })
                .ToList()
            : [];

        return new TranscriptionResponse
        {
            Text = root.TryGetProperty("text", out var t)
                ? t.GetString() ?? ""
                : string.Join(" ", segments.Select(a => a.Text)),

            Segments = segments,

            Language = root.TryGetProperty("language", out var lang)
                ? lang.GetString()
                : null,

            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Headers = headers,
                Body = json
            }
        };
    }

}
