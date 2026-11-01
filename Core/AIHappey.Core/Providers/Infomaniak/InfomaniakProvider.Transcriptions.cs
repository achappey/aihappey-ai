using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider
{
    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        if (request.Audio is null)
            throw new ArgumentException("Audio is required.", nameof(request));

        var metadata = request.GetProviderMetadata<JsonElement>(GetIdentifier());
        if (metadata.ValueKind is not (JsonValueKind.Object or JsonValueKind.Undefined))
            throw new JsonException("Infomaniak provider metadata must be a JSON object.");

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        _ = Convert.FromBase64String(audioString);

        var requestBody = BuildTranscriptionRequestBody(request.Model, audioString, metadata);
        var productId = await GetProductIdAsync(cancellationToken);
        var createBatch = await CreateTranscriptionBatchAsync(productId, requestBody, cancellationToken);
        var batchId = createBatch.BatchId;
        var resultRoot = await PollBatchResultUntilDoneAsync(productId, batchId, cancellationToken);

        return ConvertTranscriptionResult(resultRoot, request.Model.ToModelId(GetIdentifier()),
            now, warnings, createBatch.RequestBody, GetIdentifier());
    }

    private async Task<(string BatchId, string RequestBody)> CreateTranscriptionBatchAsync(
        int productId,
        string requestBody,
        CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"1/ai/{productId}/openai/audio/transcriptions")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"Infomaniak transcription create failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var batchId = root.TryGetProperty("batch_id", out var batchIdEl) && batchIdEl.ValueKind == JsonValueKind.String
            ? (batchIdEl.GetString() ?? string.Empty)
            : string.Empty;

        if (string.IsNullOrWhiteSpace(batchId))
            throw new InvalidOperationException($"Infomaniak transcription create response missing batch_id. Body: {raw}");

        return (batchId, requestBody);
    }

    private static string BuildTranscriptionRequestBody(string model, string audioBase64, JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
                payload[property.Name] = property.Value;
        }

        payload["file"] = audioBase64;
        payload["model"] = model;

        SetStringMetadata(metadata, payload, "language");
        SetStringMetadata(metadata, payload, "prompt");
        SetStringMetadata(metadata, payload, "response_format");
        SetStringMetadata(metadata, payload, "append_punctuations");
        SetStringMetadata(metadata, payload, "prepend_punctuations");
        SetArrayMetadata(metadata, payload, "timestamp_granularities");
        SetValueMetadata<int?>(metadata, payload, "chunk_length");
        SetValueMetadata<bool?>(metadata, payload, "highlight_words");
        SetValueMetadata<int?>(metadata, payload, "max_line_count");
        SetValueMetadata<int?>(metadata, payload, "max_line_width");
        SetValueMetadata<int?>(metadata, payload, "max_words_per_line");
        SetValueMetadata<double?>(metadata, payload, "no_speech_threshold");

        return JsonSerializer.Serialize(payload, JsonSerializerOptions.Web);
    }

    private static void SetStringMetadata(JsonElement metadata, Dictionary<string, object?> payload, string name)
    {
        var value = ReadMetadataValue<string?>(metadata, name);
        SetMappedMetadata(metadata, payload, name, string.IsNullOrWhiteSpace(value) ? null : value);
    }

    private static void SetArrayMetadata(JsonElement metadata, Dictionary<string, object?> payload, string name)
    {
        var value = ReadMetadataValue<IEnumerable<string>?>(metadata, name)?.ToArray();
        SetMappedMetadata(metadata, payload, name, value is { Length: > 0 } ? value : null);
    }

    private static void SetValueMetadata<T>(JsonElement metadata, Dictionary<string, object?> payload, string name)
    {
        var value = ReadMetadataValue<T>(metadata, name);
        SetMappedMetadata(metadata, payload, name, value);
    }

    private static T? ReadMetadataValue<T>(JsonElement metadata, string name)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            return default;

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

    private static void SetMappedMetadata(
        JsonElement metadata,
        Dictionary<string, object?> payload,
        string name,
        object? value)
    {
        if (metadata.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in metadata.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    payload.Remove(property.Name);
            }
        }

        if (value is not null)
            payload[name] = value;
    }

    private async Task<JsonElement> PollBatchResultUntilDoneAsync(int productId, string batchId, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var maxDelay = TimeSpan.FromSeconds(5);
        var maxWait = TimeSpan.FromMinutes(10);
        var start = DateTime.UtcNow;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var req = new HttpRequestMessage(HttpMethod.Get, $"1/ai/{productId}/results/{Uri.EscapeDataString(batchId)}");
            using var resp = await _client.SendAsync(req, cancellationToken);
            var raw = await resp.Content.ReadAsStringAsync(cancellationToken);

            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Infomaniak transcription poll failed: {(int)resp.StatusCode} {resp.ReasonPhrase}: {raw}");

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            var status = root.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
                ? (statusEl.GetString() ?? string.Empty)
                : string.Empty;

            if (string.Equals(status, "success", StringComparison.OrdinalIgnoreCase))
                return root.Clone();

            if (string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase)
                || string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Infomaniak transcription batch ended with status '{status}'. Body: {raw}");
            }

            if (DateTime.UtcNow - start > maxWait)
                throw new TimeoutException($"Infomaniak transcription did not complete within {maxWait.TotalMinutes} minutes. Last status='{status}'.");

            await Task.Delay(delay, cancellationToken);
            delay = delay < maxDelay
                ? TimeSpan.FromSeconds(Math.Min(maxDelay.TotalSeconds, delay.TotalSeconds * 1.5))
                : maxDelay;
        }
    }

    private static TranscriptionResponse ConvertTranscriptionResult(
        JsonElement batchResultRoot,
        string model,
        DateTime now,
        IEnumerable<object> warnings,
        string requestBody,
        string providerId)
    {
        if (!batchResultRoot.TryGetProperty("data", out var dataEl))
            throw new InvalidOperationException("Infomaniak transcription result does not contain 'data'.");

        if (TryParseStructuredTranscription(dataEl, out var structured))
        {
            return new TranscriptionResponse
            {
                Text = structured.Text,
                Language = structured.Language,
                DurationInSeconds = structured.DurationInSeconds,
                Segments = structured.Segments,
                Warnings = warnings,
                ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
                Response = new ResponseData
                {
                    Timestamp = now,
                    ModelId = model,
                    Body = batchResultRoot.Clone()
                },
                Request = new TranscriptionRequestItem
                {
                    Body = requestBody
                }
            };
        }

        var plainText = dataEl.ValueKind == JsonValueKind.String
            ? (dataEl.GetString() ?? string.Empty)
            : dataEl.GetRawText();

        return new TranscriptionResponse
        {
            Text = plainText,
            Language = null,
            DurationInSeconds = null,
            Segments = [],
            Warnings = warnings,
            ProviderMetadata = providerId.CreatePrimitiveProviderMetadata(),
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = model,
                Body = batchResultRoot.Clone()
            },
            Request = new TranscriptionRequestItem
            {
                Body = requestBody
            }
        };
    }

    private static bool TryParseStructuredTranscription(JsonElement dataEl, out (string Text, string? Language, float? DurationInSeconds, List<TranscriptionSegment> Segments) result)
    {
        result = (string.Empty, null, null, []);

        JsonElement root;
        JsonDocument? parsedDoc = null;
        try
        {
            if (dataEl.ValueKind == JsonValueKind.Object)
            {
                root = dataEl;
            }
            else if (dataEl.ValueKind == JsonValueKind.String)
            {
                var str = dataEl.GetString();
                if (string.IsNullOrWhiteSpace(str))
                    return false;

                str = str.Trim();
                if (!(str.StartsWith('{') && str.EndsWith('}')))
                    return false;

                parsedDoc = JsonDocument.Parse(str);
                root = parsedDoc.RootElement;
            }
            else
            {
                return false;
            }

            var segments = new List<TranscriptionSegment>();

            if (root.TryGetProperty("segments", out var segmentsEl) && segmentsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var seg in segmentsEl.EnumerateArray())
                {
                    var segmentText = seg.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String
                        ? (textEl.GetString() ?? string.Empty)
                        : string.Empty;

                    if (string.IsNullOrWhiteSpace(segmentText))
                        continue;

                    var start = seg.TryGetProperty("start", out var startEl) && startEl.ValueKind == JsonValueKind.Number
                        ? (float)startEl.GetDouble()
                        : 0f;

                    var end = seg.TryGetProperty("end", out var endEl) && endEl.ValueKind == JsonValueKind.Number
                        ? (float)endEl.GetDouble()
                        : start;

                    segments.Add(new TranscriptionSegment
                    {
                        Text = segmentText,
                        StartSecond = start,
                        EndSecond = end
                    });
                }
            }

            var text = root.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String
                ? (t.GetString() ?? string.Empty)
                : string.Join(" ", segments.Select(a => a.Text));

            var language = root.TryGetProperty("language", out var lang) && lang.ValueKind == JsonValueKind.String
                ? lang.GetString()
                : null;

            float? duration = null;
            if (root.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Number)
            {
                duration = (float)dur.GetDouble();
            }
            else if (root.TryGetProperty("duration_in_seconds", out var durS) && durS.ValueKind == JsonValueKind.Number)
            {
                duration = (float)durS.GetDouble();
            }

            result = (text, language, duration, segments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        finally
        {
            parsedDoc?.Dispose();
        }
    }
}
