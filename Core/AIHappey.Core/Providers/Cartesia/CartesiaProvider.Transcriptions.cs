using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider
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

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.Object))
            throw new JsonException("Cartesia provider options must be a JSON object.");

        if (request.Model.StartsWith(CartesiaTranscriptionModelPrefix, StringComparison.OrdinalIgnoreCase))
            request.Model = request.Model[CartesiaTranscriptionModelPrefix.Length..].Trim();

        if (!SupportedTranscriptionModelIds.Any(m => string.Equals(m, request.Model, StringComparison.OrdinalIgnoreCase)))
            throw new NotSupportedException($"{ProviderName} transcription model '{request.Model}' is not supported.");

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

        var query = new List<string>();
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (TryGetMetadataProperty(metadata, "encoding", out var encodingElement))
            {
                var encoding = DeserializeMetadataValue<string?>(encodingElement);
                if (!string.IsNullOrWhiteSpace(encoding))
                    query.Add($"encoding={Uri.EscapeDataString(encoding.Trim())}");
            }

            if (TryGetMetadataProperty(metadata, "sampleRate", out var sampleRateElement))
            {
                var sampleRate = DeserializeMetadataValue<int?>(sampleRateElement);
                if (sampleRate is not null)
                    query.Add($"sample_rate={sampleRate.Value}");
            }
        }

        var path = query.Count == 0
            ? "stt"
            : "stt?" + string.Join("&", query);

        using var form = new MultipartFormDataContent();

        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(request.MediaType);

        form.Add(file, "file", fileName);
        form.Add(new StringContent(request.Model), "model");

        string? apiVersion = null;
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            if (TryGetMetadataProperty(metadata, "apiVersion", out var apiVersionElement))
                apiVersion = DeserializeMetadataValue<string?>(apiVersionElement);

            if (TryGetMetadataProperty(metadata, "language", out var languageElement))
            {
                var language = DeserializeMetadataValue<string?>(languageElement);
                if (!string.IsNullOrWhiteSpace(language))
                    form.Add(new StringContent(language.Trim()), "language");
            }

            if (TryGetMetadataProperty(metadata, "timestampGranularities", out var granularitiesElement))
            {
                var granularities = DeserializeMetadataValue<string[]?>(granularitiesElement);
                if (granularities is not null)
                {
                    foreach (var granularity in granularities.Where(g => !string.IsNullOrWhiteSpace(g)))
                        form.Add(new StringContent(granularity.Trim()), "timestamp_granularities[]");
                }
            }

            foreach (var property in metadata.EnumerateObject())
                if (!IsMappedMetadataProperty(property.Name))
                    form.Add(new StringContent(GetMultipartValue(property.Value)), property.Name);
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = form
        };

        ApplyVersionHeader(httpRequest, apiVersion);

        using var resp = await _client.SendAsync(httpRequest, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} STT failed ({(int)resp.StatusCode}): {json}");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
            ? textEl.GetString() ?? string.Empty
            : string.Empty;

        var language = root.TryGetProperty("language", out var langEl) && langEl.ValueKind == JsonValueKind.String
            ? langEl.GetString()
            : null;

        float? duration = null;
        if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            duration = (float)durEl.GetDouble();

        var segments = new List<TranscriptionSegment>();
        if (root.TryGetProperty("words", out var wordsEl) && wordsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var word in wordsEl.EnumerateArray())
            {
                var wordText = word.TryGetProperty("word", out var wEl) && wEl.ValueKind == JsonValueKind.String
                    ? wEl.GetString() ?? string.Empty
                    : string.Empty;

                var start = word.TryGetProperty("start", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? (float)sEl.GetDouble()
                    : 0f;

                var end = word.TryGetProperty("end", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                    ? (float)eEl.GetDouble()
                    : start;

                if (string.IsNullOrWhiteSpace(wordText))
                    continue;

                segments.Add(new TranscriptionSegment
                {
                    Text = wordText,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = warnings,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static T? DeserializeMetadataValue<T>(JsonElement value)
        => value.ValueKind == JsonValueKind.Null
            ? default
            : JsonSerializer.Deserialize<T>(value.GetRawText(), JsonSerializerOptions.Web);

    private static bool TryGetMetadataProperty(JsonElement metadata, string name, out JsonElement value)
    {
        foreach (var property in metadata.EnumerateObject().Reverse())
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool IsMappedMetadataProperty(string name)
        => name.Equals("apiVersion", StringComparison.Ordinal)
            || name.Equals("language", StringComparison.Ordinal)
            || name.Equals("encoding", StringComparison.Ordinal)
            || name.Equals("sampleRate", StringComparison.Ordinal)
            || name.Equals("timestampGranularities", StringComparison.Ordinal);

    private static string GetMultipartValue(JsonElement value)
        => value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : value.GetRawText();
}
