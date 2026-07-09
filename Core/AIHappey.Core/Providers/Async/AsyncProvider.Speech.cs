using System.Text;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Async;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var metadata = request.GetProviderMetadata<AsyncSpeechProviderMetadata>(GetIdentifier());
        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        var (modelId, modelVoiceId) = ParseAsyncSpeechModelAndVoice(request.Model);
        var voiceId = modelVoiceId ?? request.Voice?.Trim() ?? metadata?.Voice?.Id?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("'voice' (async voice UUID) is required.");

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(modelVoiceId, request.Voice.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var container = (request.OutputFormat ?? metadata?.OutputFormat?.Container ?? "mp3").Trim().ToLowerInvariant();
        container = container is "mp3" or "wav" or "raw" ? container : "mp3";

        var sampleRate = metadata?.OutputFormat?.SampleRate ?? 44100;
        var bitRate = metadata?.OutputFormat?.BitRate ?? 192000;
        var encoding = metadata?.OutputFormat?.Encoding ?? "pcm_s16le";

        var outputFormat = new Dictionary<string, object?>
        {
            ["container"] = container,
            ["sample_rate"] = sampleRate,
        };

        if (container == "mp3")
        {
            outputFormat["bit_rate"] = bitRate;
        }
        else
        {
            outputFormat["encoding"] = encoding;
        }

        var language = request.Language ?? metadata?.Language;

        var speedControl = metadata?.SpeedControl;
        if (request.Speed is not null)
            speedControl = request.Speed.Value;

        var body = new Dictionary<string, object?>
        {
            ["model_id"] = modelId,
            ["transcript"] = request.Text,
            ["voice"] = new Dictionary<string, object?>
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["output_format"] = outputFormat,
            ["language"] = string.IsNullOrWhiteSpace(language) ? null : language
        };

        if (speedControl is not null)
            body["speed_control"] = speedControl;

        if (metadata?.Stability is not null)
            body["stability"] = metadata.Stability;

        using var content = new StringContent(
            JsonSerializer.Serialize(body),
            Encoding.UTF8,
            "application/json");

        using var resp = await _client.PostAsync("text_to_speech", content, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"asyncAI TTS failed ({(int)resp.StatusCode}): {err}");
        }

        var mime = container switch
        {
            "wav" => "audio/wav",
            "raw" => "application/octet-stream",
            _ => "audio/mpeg"
        };

        var audio = Convert.ToBase64String(bytes);

        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = audio,
                MimeType = mime,
                Format = container ?? "mp3"
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = modelId,
                    voice = voiceId
                }, JsonSerializerOptions.Web)
            },
            Request = new()
            {
                Body = body
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = $"{modelId}/{voiceId}".ToModelId(GetIdentifier()),
            }
        };
    }

    private string NormalizeAsyncModelId(string model)
    {
        var normalized = model.Trim();
        var providerPrefix = GetIdentifier() + "/";
        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..].Trim();

        return normalized;
    }

    private (string ModelId, string? VoiceId) ParseAsyncSpeechModelAndVoice(string model)
    {
        var normalized = NormalizeAsyncModelId(model);

        foreach (var speechModel in AsyncSpeechModelIds)
        {
            if (string.Equals(normalized, speechModel, StringComparison.OrdinalIgnoreCase))
                return (speechModel, null);

            var prefix = speechModel + "/";
            if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            var voiceId = normalized[prefix.Length..].Trim();
            if (string.IsNullOrWhiteSpace(voiceId))
                throw new ArgumentException($"Async speech shortcut model must be in the form '{{model}}/{{voice_id}}'.", nameof(model));

            return (speechModel, voiceId);
        }

        throw new NotSupportedException($"Async speech model '{model}' is not supported. Use one of: {string.Join(", ", AsyncSpeechModelIds)} or '{{model}}/{{voice_id}}'.");
    }
}

