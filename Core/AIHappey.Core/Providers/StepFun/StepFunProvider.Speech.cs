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

        var voice = !string.IsNullOrWhiteSpace(request.Voice)
            ? request.Voice.Trim()
            : metadata?.Voice?.Trim();

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
            ["model"] = request.Model,
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

