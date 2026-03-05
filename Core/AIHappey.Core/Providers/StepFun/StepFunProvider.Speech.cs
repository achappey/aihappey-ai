using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.StepFun;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.StepFun;

public partial class StepFunProvider
{
    private static readonly JsonSerializerOptions StepFunSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<StepFunSpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceId) = ParseSpeechModelAndVoice(request.Model);

        var voice = (modelVoiceId ?? request.Voice ?? metadata?.Voice)?.Trim();

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.Voice)
                && !string.Equals(metadata.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.stepfun.voice", reason = "voice is derived from model id" });
            }
        }

        if (string.IsNullOrWhiteSpace(voice))
            throw new ArgumentException("Voice is required for StepFun speech endpoint.", nameof(request));

        var outputFormat = !string.IsNullOrWhiteSpace(request.OutputFormat)
            ? request.OutputFormat.Trim().ToLowerInvariant()
            : !string.IsNullOrWhiteSpace(metadata?.ResponseFormat)
                ? metadata!.ResponseFormat!.Trim().ToLowerInvariant()
                : "mp3";

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(metadata?.StreamFormat) &&
            !string.Equals(metadata.StreamFormat, "audio", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "unsupported", feature = "stream_format" });
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = baseModelId,
            ["input"] = request.Text,
            ["voice"] = voice,
            ["response_format"] = outputFormat
        };

        var speed = request.Speed ?? metadata?.Speed;
        if (speed is not null)
            payload["speed"] = Math.Clamp(speed.Value, 0.5f, 2.0f);

        if (metadata?.Volume is not null)
            payload["volume"] = metadata.Volume.Value;

        if (metadata?.SampleRate is not null)
            payload["sample_rate"] = metadata.SampleRate.Value;

        var voiceLabel = NormalizeVoiceLabel(metadata?.VoiceLabel, request.Language);
        if (voiceLabel is not null)
            payload["voice_label"] = voiceLabel;

        if (metadata?.PronunciationMap?.Tone is { Length: > 0 })
            payload["pronunciation_map"] = metadata.PronunciationMap;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, StepFunSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"StepFun speech request failed ({(int)resp.StatusCode}): {err}");
        }

        var contentType = resp.Content.Headers.ContentType?.MediaType;

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = contentType ?? OpenAI.OpenAIProvider.MapToAudioMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    statusCode = (int)resp.StatusCode,
                    contentType,
                    contentLength = bytes.LongLength
                }
            }
        };
    }

    private (string BaseModelId, string? VoiceId) ParseSpeechModelAndVoice(string model)
    {
        var localModel = ExtractProviderLocalModelId(model);
        if (string.IsNullOrWhiteSpace(localModel))
            throw new ArgumentException("Model is required.", nameof(model));

        var slashIndex = localModel.IndexOf('/');
        if (slashIndex < 0)
            return (localModel, null);

        if (slashIndex == 0 || slashIndex >= localModel.Length - 1)
            throw new ArgumentException("StepFun speech model must include both base model id and voice id in the form '[baseModel]/[voiceId]'.", nameof(model));

        var baseModelId = localModel[..slashIndex].Trim();
        var voiceId = localModel[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(baseModelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("StepFun speech model must include both base model id and voice id in the form '[baseModel]/[voiceId]'.", nameof(model));

        return (baseModelId, voiceId);
    }

    private string ExtractProviderLocalModelId(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return string.Empty;

        var providerPrefix = GetIdentifier() + "/";
        var trimmed = modelId.Trim();

        return trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[providerPrefix.Length..]
            : trimmed;
    }

    private static StepFunSpeechVoiceLabel? NormalizeVoiceLabel(StepFunSpeechVoiceLabel? label, string? requestLanguage)
    {
        var language = label?.Language;
        var emotion = label?.Emotion;
        var style = label?.Style;

        if (string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(requestLanguage))
            language = requestLanguage;

        if (!string.IsNullOrWhiteSpace(language))
            return new StepFunSpeechVoiceLabel { Language = language };

        if (!string.IsNullOrWhiteSpace(emotion))
            return new StepFunSpeechVoiceLabel { Emotion = emotion };

        if (!string.IsNullOrWhiteSpace(style))
            return new StepFunSpeechVoiceLabel { Style = style };

        return null;
    }
}

