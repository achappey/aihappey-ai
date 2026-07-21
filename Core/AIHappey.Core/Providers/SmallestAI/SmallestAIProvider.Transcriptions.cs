using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private const string PulseProModel = "pulse-pro";
    private const string TranscriptionPath = "v1/stt/";

    public async Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.MediaType))
            throw new ArgumentException("MediaType is required.", nameof(request));

        var model = NormalizeTranscriptionModel(request.Model);

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (MediaContentHelpers.TryParseDataUrl(audioString, out _, out var parsedBase64))
            audioString = parsedBase64;

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(audioString);
        }
        catch (FormatException ex)
        {
            throw new ArgumentException("Audio must be valid base64 or data URL base64.", nameof(request), ex);
        }

        var metadata = request.GetProviderMetadata<SmallestAITranscriptionProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;

        var language = !string.IsNullOrWhiteSpace(metadata?.Language)
            ? metadata!.Language!.Trim()
            : "en";

        if (string.Equals(model, PulseProModel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(language, "en", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{ProviderName} '{PulseProModel}' supports English only; use language 'en'.", nameof(request));
        }

        var query = new List<string>
        {
            $"model={Uri.EscapeDataString(model)}",
            $"language={Uri.EscapeDataString(language)}"
        };

        AppendBoolQuery(query, "word_timestamps", metadata?.WordTimestamps);
        AppendBoolQuery(query, "diarize", metadata?.Diarize);
        AppendBoolQuery(query, "gender_detection", metadata?.GenderDetection);
        AppendBoolQuery(query, "emotion_detection", metadata?.EmotionDetection);
        AppendBoolQuery(query, "redact_pii", metadata?.RedactPii);
        AppendBoolQuery(query, "redact_pci", metadata?.RedactPci);

        if (!string.IsNullOrWhiteSpace(metadata?.WebhookUrl))
        {
            if (!string.Equals(model, PulseProModel, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{ProviderName} webhooks are supported only by '{PulseProModel}'.", nameof(request));

            query.Add($"webhook_url={Uri.EscapeDataString(metadata.WebhookUrl)}");
        }
        if (!string.IsNullOrWhiteSpace(metadata?.WebhookMethod))
        {
            if (!string.Equals(model, PulseProModel, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{ProviderName} webhook methods are supported only by '{PulseProModel}'.", nameof(request));

            var webhookMethod = metadata.WebhookMethod.Trim().ToUpperInvariant();
            if (webhookMethod is not ("GET" or "POST"))
                throw new ArgumentException($"{ProviderName} webhookMethod must be 'GET' or 'POST'.", nameof(request));

            query.Add($"webhook_method={Uri.EscapeDataString(webhookMethod)}");
        }
        if (!string.IsNullOrWhiteSpace(metadata?.WebhookExtra))
        {
            if (!string.Equals(model, PulseProModel, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"{ProviderName} webhook metadata is supported only by '{PulseProModel}'.", nameof(request));

            query.Add($"webhook_extra={Uri.EscapeDataString(metadata.WebhookExtra)}");
        }

        var endpoint = $"{TranscriptionPath}?{string.Join("&", query)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new ByteArrayContent(bytes)
        };
        httpRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var responseText = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} pulse transcription failed ({(int)resp.StatusCode}): {responseText}");

        using var doc = JsonDocument.Parse(responseText);
        var root = doc.RootElement;

        var text = ReadString(root, "transcription") ?? string.Empty;
        var responseLanguage = ReadString(root, "language") ?? language;

        float? duration = null;
        if (TryGetPropertyIgnoreCase(root, "metadata", out var metadataEl)
            && metadataEl.ValueKind == JsonValueKind.Object)
        {
            if (TryGetPropertyIgnoreCase(metadataEl, "duration", out var durEl)
                && durEl.ValueKind == JsonValueKind.Number)
                duration = (float)durEl.GetDouble();
        }

        var segments = ParseSegments(root);

        return new TranscriptionResponse
        {
            Text = text,
            Language = responseLanguage,
            DurationInSeconds = duration,
            Segments = segments,
            Warnings = [],
            ProviderMetadata = GetIdentifier()
            .CreatePrimitiveProviderMetadata(),
            Response = new()
            {
                Timestamp = now,
                Headers = resp.GetHeaders(),
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    private static string NormalizeTranscriptionModel(string model)
    {
        var normalized = model.Trim();

        if (string.Equals(normalized, PulseModel, StringComparison.OrdinalIgnoreCase))
            return PulseModel;
        if (string.Equals(normalized, PulseProModel, StringComparison.OrdinalIgnoreCase))
            return PulseProModel;

        throw new NotSupportedException(
            $"{ProviderName} transcription model '{model}' is not supported. Use '{PulseModel}' or '{PulseProModel}'.");
    }

    private static void AppendBoolQuery(List<string> query, string key, bool? value)
    {
        if (value is null)
            return;

        query.Add($"{Uri.EscapeDataString(key)}={(value.Value ? "true" : "false")}");
    }

    private static List<TranscriptionSegment> ParseSegments(JsonElement root)
    {
        var segments = new List<TranscriptionSegment>();

        if (TryGetPropertyIgnoreCase(root, "utterances", out var utterances)
            && utterances.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in utterances.EnumerateArray())
            {
                var text = ReadString(u, "text");
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var start = ReadFloat(u, "start") ?? 0f;
                var end = ReadFloat(u, "end") ?? start;

                if (!string.IsNullOrWhiteSpace(ReadString(u, "speaker")))
                    text = $"{ReadString(u, "speaker")}: {text}";

                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start,
                    EndSecond = end
                });
            }

            if (segments.Count > 0)
                return segments;
        }

        if (TryGetPropertyIgnoreCase(root, "words", out var words)
            && words.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in words.EnumerateArray())
            {
                var text = ReadString(w, "word");
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var start = ReadFloat(w, "start") ?? 0f;
                var end = ReadFloat(w, "end") ?? start;
                segments.Add(new TranscriptionSegment
                {
                    Text = text,
                    StartSecond = start,
                    EndSecond = end
                });
            }
        }

        return segments;
    }

    private static float? ReadFloat(JsonElement obj, string name)
    {
        if (!TryGetPropertyIgnoreCase(obj, name, out var el))
            return null;

        if (el.ValueKind == JsonValueKind.Number)
            return (float)el.GetDouble();

        if (el.ValueKind == JsonValueKind.String && float.TryParse(el.GetString(), out var parsed))
            return parsed;

        return null;
    }
}

