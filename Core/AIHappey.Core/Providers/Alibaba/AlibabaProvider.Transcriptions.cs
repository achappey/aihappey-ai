using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Alibaba;
using AIHappey.Core.AI;
using AIHappey.Core.MCP.Media;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider
{
    private static readonly HashSet<string> LiveTranslateModels = new(StringComparer.OrdinalIgnoreCase)
    {
        "qwen3-livetranslate-flash",
        "qwen3-livetranslate-flash-2025-12-01"
    };

    private const string LiveTranslateSuffix = "/translate-to-";

    private async Task<TranscriptionResponse> TranscriptionRequestInternal(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model;
        if (!string.IsNullOrWhiteSpace(model) && model.Contains('/'))
        {
            var split = model.SplitModelId();
            model = string.Equals(split.Provider, GetIdentifier(), StringComparison.OrdinalIgnoreCase)
                ? split.Model
                : request.Model;
        }

        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(request));

        var isLiveTranslate = TryParseLiveTranslateModel(model, out var liveTranslateModel, out var targetLanguage);
        if (isLiveTranslate)
        {
            if (!LiveTranslateModels.Contains(liveTranslateModel))
                throw new NotSupportedException($"Alibaba live-translate model '{model}' is not supported.");
            if (string.IsNullOrWhiteSpace(targetLanguage))
                throw new ArgumentException("Target language is required for Alibaba live-translate models.", nameof(request));

            model = liveTranslateModel;
        }
        else if (!string.Equals(model, "qwen3-asr-flash", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Alibaba transcription model '{model}' is not supported.");
        }

        var audioString = request.Audio switch
        {
            JsonElement je when je.ValueKind == JsonValueKind.String => je.GetString(),
            _ => request.Audio?.ToString()
        };

        if (string.IsNullOrWhiteSpace(audioString))
            throw new ArgumentException("Audio is required.", nameof(request));

        if (!MediaContentHelpers.TryParseDataUrl(audioString, out _, out _))
            throw new ArgumentException("Audio must be provided as a data URL (data:audio/...;base64,...).", nameof(request));

        var metadata = request.GetProviderMetadata<AlibabaTranscriptionProviderMetadata>(GetIdentifier());

        var asrOptions = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(metadata?.Language))
            asrOptions["language"] = metadata.Language;
        if (metadata?.EnableItn is not null)
            asrOptions["enable_itn"] = metadata.EnableItn.Value;

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new { data = audioString }
                        }
                    }
                }
            },
            ["stream"] = false
        };

        if (!isLiveTranslate && asrOptions.Count > 0)
            payload["asr_options"] = asrOptions;

        if (isLiveTranslate)
            payload["translation_options"] = new { target_lang = targetLanguage };

        using var req = new HttpRequestMessage(HttpMethod.Post, "compatible-mode/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOpts), Encoding.UTF8, "application/json")
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var json = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Alibaba STT failed ({(int)resp.StatusCode}): {json}");

        return ConvertTranscriptionResponse(json, model);
    }

    private static bool TryParseLiveTranslateModel(
        string model,
        out string baseModel,
        out string targetLanguage)
    {
        baseModel = model;
        targetLanguage = string.Empty;

        var idx = model.IndexOf(LiveTranslateSuffix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
            return false;

        baseModel = model[..idx];
        targetLanguage = model[(idx + LiveTranslateSuffix.Length)..];

        return true;
    }

    private static TranscriptionResponse ConvertTranscriptionResponse(string json, string model)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string text = string.Empty;
        string? language = null;
        float? durationSeconds = null;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message))
            {
                if (message.TryGetProperty("content", out var content))
                    text = content.GetString() ?? string.Empty;

                if (message.TryGetProperty("annotations", out var annotations) &&
                    annotations.ValueKind == JsonValueKind.Array &&
                    annotations.GetArrayLength() > 0)
                {
                    var annotation = annotations[0];
                    if (annotation.TryGetProperty("language", out var langEl))
                        language = langEl.GetString();
                }
            }
        }

        if (root.TryGetProperty("usage", out var usage) &&
            usage.TryGetProperty("seconds", out var secondsEl) &&
            secondsEl.ValueKind == JsonValueKind.Number)
        {
            durationSeconds = secondsEl.GetSingle();
        }

        return new TranscriptionResponse
        {
            Text = text,
            Language = language,
            DurationInSeconds = durationSeconds,
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                ModelId = model,
                Body = root.Clone()
            }
        };
    }
}
