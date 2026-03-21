using System.Net.Http.Json;
using System.Text.Json;
using AIHappey.Common.Model.Providers.Vogent;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Vogent;

public partial class VogentProvider
{
    private const string ProviderName = "Vogent";
    private const string BaseSpeechModel = "tts";

    private async Task<SpeechResponse> SpeechRequestInternal(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<VogentSpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceId) = ParseSpeechModelAndVoice(request.Model);

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} speech model '{request.Model}' is not supported.");

        var voiceId = (modelVoiceId ?? request.Voice ?? metadata?.VoiceId)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required. Provide request.voice, providerOptions.vogent.voiceId, or a model like 'vogent/tts/{voiceId}'.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(metadata?.VoiceId)
            && !string.Equals(metadata.VoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "providerOptions.vogent.voiceId", reason = "voice is derived from model id" });
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        if (request.Speed is not null)
            warnings.Add(new { type = "unsupported", feature = "speed" });

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var format = BuildOutputFormat(request.OutputFormat, metadata);
        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voiceId"] = voiceId,
        };

        if (format is not null)
            payload["format"] = format;

        if (metadata?.VoiceOptionValues is { Count: > 0 })
        {
            payload["voiceOptionValues"] = metadata.VoiceOptionValues
                .Select(v => new Dictionary<string, string>
                {
                    ["optionId"] = v.OptionId,
                    ["value"] = v.Value
                })
                .ToArray();
        }

        using var resp = await _client.PostAsJsonAsync("api/tts", payload, JsonSerializerOptions.Web, cancellationToken);
        var bytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var body = System.Text.Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");
        }

        var resolvedFormat = format?.OutputType?.ToLowerInvariant() switch
        {
            "raw_pcm16" => "pcm",
            "wav_pcm16" => "wav",
            "mp3" => "mp3",
            _ => "wav"
        };

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["voiceId"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web),
            ["outputType"] = JsonSerializer.SerializeToElement(format?.OutputType ?? "WAV_PCM16", JsonSerializerOptions.Web),
            ["sampleRate"] = JsonSerializer.SerializeToElement(format?.SampleRate ?? 24000, JsonSerializerOptions.Web)
        };

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(bytes),
                MimeType = ResolveSpeechMimeType(resolvedFormat, resp.Content.Headers.ContentType?.MediaType),
                Format = resolvedFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = "api/tts",
                    status = (int)resp.StatusCode,
                    contentType = resp.Content.Headers.ContentType?.MediaType
                }
            }
        };
    }

    private static VogentOutputFormat? BuildOutputFormat(string? outputFormat, VogentSpeechProviderMetadata? metadata)
    {
        var normalizedOutputType = NormalizeOutputType(outputFormat ?? metadata?.OutputType);
        var sampleRate = metadata?.SampleRate;

        if (normalizedOutputType is null && sampleRate is null)
            return null;

        return new VogentOutputFormat
        {
            OutputType = normalizedOutputType ?? "WAV_PCM16",
            SampleRate = sampleRate ?? 24000
        };
    }

    private static string? NormalizeOutputType(string? outputType)
    {
        if (string.IsNullOrWhiteSpace(outputType))
            return null;

        return outputType.Trim().ToUpperInvariant() switch
        {
            "PCM" or "RAW" or "RAW_PCM16" => "RAW_PCM16",
            "WAV" or "WAV_PCM16" => "WAV_PCM16",
            "MP3" => "MP3",
            var value => value
        };
    }

    private static string ResolveSpeechMimeType(string outputFormat, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType!;

        return outputFormat.ToLowerInvariant() switch
        {
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
    }

    private static (string BaseModelId, string? VoiceId) ParseSpeechModelAndVoice(string model)
    {
        var raw = model.Trim();

        var parts = raw.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => (parts[0], null),
            2 => (parts[0], parts[1]),
            _ => throw new ArgumentException("Vogent speech model must be either 'tts' or 'tts/{voiceId}'.", nameof(model))
        };
    }

    private sealed class VogentOutputFormat
    {
        public string OutputType { get; set; } = null!;
        public int SampleRate { get; set; }
    }
}
