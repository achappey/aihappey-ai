using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Typecast;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Typecast;

public partial class TypecastProvider
{
    private static readonly JsonSerializerOptions TypecastSpeechJson = new(JsonSerializerDefaults.Web)
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

        var (modelId, voiceId) = ParseModelAndVoiceFromModel(request.Model);
        var metadata = request.GetProviderMetadata<TypecastSpeechProviderMetadata>(GetIdentifier());

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (!string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), voiceId, StringComparison.OrdinalIgnoreCase))
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });

        if (request.Speed is { } speed)
            warnings.Add(new { type = "ignored", feature = "speed", reason = "Typecast uses output.audio_tempo via providerOptions.typecast.audioTempo" });

        var language = !string.IsNullOrWhiteSpace(request.Language)
            ? request.Language!.Trim()
            : metadata?.Language?.Trim();

        var outputFormat = NormalizeOutputFormat(request.OutputFormat);

        var volume = metadata?.Volume ?? 100;
        if (volume is < 0 or > 200)
            throw new ArgumentOutOfRangeException(nameof(TypecastSpeechProviderMetadata.Volume), "Typecast volume must be between 0 and 200.");

        var audioPitch = metadata?.AudioPitch ?? 0;
        if (audioPitch is < -12 or > 12)
            throw new ArgumentOutOfRangeException(nameof(TypecastSpeechProviderMetadata.AudioPitch), "Typecast audioPitch must be between -12 and 12.");

        var audioTempo = metadata?.AudioTempo ?? 1f;
        if (audioTempo is < 0.5f or > 2f)
            throw new ArgumentOutOfRangeException(nameof(TypecastSpeechProviderMetadata.AudioTempo), "Typecast audioTempo must be between 0.5 and 2.0.");

        var prompt = BuildPrompt(metadata, modelId);
        if (prompt is null && string.Equals(modelId, "ssfm-v30", StringComparison.OrdinalIgnoreCase))
        {
            prompt = new Dictionary<string, object?>
            {
                ["emotion_type"] = "smart"
            };
        }

        var payload = new Dictionary<string, object?>
        {
            ["voice_id"] = voiceId,
            ["text"] = request.Text,
            ["model"] = modelId,
            ["language"] = language,
            ["prompt"] = prompt,
            ["output"] = new Dictionary<string, object?>
            {
                ["volume"] = volume,
                ["audio_pitch"] = audioPitch,
                ["audio_tempo"] = audioTempo,
                ["audio_format"] = outputFormat
            },
            ["seed"] = metadata?.Seed
        };

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/text-to-speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, TypecastSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {Encoding.UTF8.GetString(bytes)}");

        var mimeType = ResolveMimeType(resp.Content.Headers.ContentType?.MediaType, outputFormat);
        var resolvedFormat = ResolveFormat(mimeType, outputFormat);

        var providerMetaPayload = new
        {
            model = modelId,
            voiceId,
            language,
            prompt,
            output = new
            {
                volume,
                audioPitch,
                audioTempo,
                audioFormat = outputFormat
            },
            seed = metadata?.Seed,
            bytes = bytes.Length
        };

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = mimeType,
                Format = resolvedFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(providerMetaPayload)
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = JsonSerializer.SerializeToElement(providerMetaPayload)
            }
        };
    }

    private static (string ModelId, string VoiceId) ParseModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(TypecastTtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{TypecastTtsModelPrefix}[model]/[voiceId]'.");

        var tail = model[TypecastTtsModelPrefix.Length..].Trim();
        var slashIndex = tail.LastIndexOf('/');

        if (slashIndex <= 0 || slashIndex >= tail.Length - 1)
            throw new ArgumentException("Model must include both model id and voice id after 'typecast/'.", nameof(model));

        var modelId = tail[..slashIndex].Trim();
        var voiceId = tail[(slashIndex + 1)..].Trim();

        if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Model must include both model id and voice id after 'typecast/'.", nameof(model));

        return (modelId, voiceId);
    }

    private static Dictionary<string, object?>? BuildPrompt(TypecastSpeechProviderMetadata? metadata, string modelId)
    {
        if (metadata is null)
            return null;

        var emotionType = metadata.EmotionType?.Trim();
        var emotion = metadata.Emotion?.Trim();
        var previousText = metadata.PreviousText?.Trim();
        var nextText = metadata.NextText?.Trim();

        if (string.IsNullOrWhiteSpace(emotionType)
            && string.IsNullOrWhiteSpace(emotion)
            && string.IsNullOrWhiteSpace(previousText)
            && string.IsNullOrWhiteSpace(nextText))
            return null;

        var prompt = new Dictionary<string, object?>();

        if (!string.IsNullOrWhiteSpace(emotionType))
            prompt["emotion_type"] = emotionType;
        else if (string.Equals(modelId, "ssfm-v30", StringComparison.OrdinalIgnoreCase))
            prompt["emotion_type"] = "smart";

        if (!string.IsNullOrWhiteSpace(emotion))
            prompt["emotion"] = emotion;

        if (!string.IsNullOrWhiteSpace(previousText))
            prompt["previous_text"] = previousText;

        if (!string.IsNullOrWhiteSpace(nextText))
            prompt["next_text"] = nextText;

        return prompt.Count == 0 ? null : prompt;
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();

        return normalized switch
        {
            "mp3" or "mpeg" => "mp3",
            _ => "wav"
        };
    }

    private static string ResolveMimeType(string? contentType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType;

        return outputFormat switch
        {
            "mp3" => "audio/mpeg",
            _ => "audio/wav"
        };
    }

    private static string ResolveFormat(string mimeType, string outputFormat)
    {
        if (!string.IsNullOrWhiteSpace(outputFormat))
            return outputFormat;

        var mt = mimeType.Trim().ToLowerInvariant();
        if (mt.Contains("mpeg") || mt.Contains("mp3")) return "mp3";
        return "wav";
    }
}

