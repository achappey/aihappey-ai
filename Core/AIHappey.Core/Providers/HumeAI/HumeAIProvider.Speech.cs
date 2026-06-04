using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.HumeAI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.HumeAI;

public partial class HumeAIProvider
{
    private const string BaseSpeechModel = "octave";

    private static readonly JsonSerializerOptions HumeJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        ApplyAuthHeader();

        var now = DateTime.UtcNow;
        var warnings = new List<object>();
        var metadata = request.GetProviderMetadata<HumeAISpeechProviderMetadata>(GetIdentifier());
        var (baseModelId, modelVoiceProvider, modelVoiceId) = ParseSpeechModel(request.Model);

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Unsupported {ProviderName} speech model '{baseModelId}'. Use '{BaseSpeechModel}'.", nameof(request));

        var outputFormat = NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat) ?? "mp3";
        var voiceProvider = NormalizeVoiceProvider(modelVoiceProvider ?? metadata?.VoiceProvider);
        var voiceId = FirstNonEmpty(modelVoiceId, request.Voice, metadata?.VoiceId);
        var voiceName = string.IsNullOrWhiteSpace(voiceId) ? FirstNonEmpty(metadata?.VoiceName) : null;
        var voice = BuildVoiceRef(voiceId, voiceName, voiceProvider);
        var description = FirstNonEmpty(request.Instructions, metadata?.Description);
        var speed = request.Speed is null ? null : (double?)request.Speed.Value;
        var version = NormalizeOctaveVersion(metadata?.Version);
        var trailingSilence = metadata?.TrailingSilence;

        if (!string.IsNullOrWhiteSpace(modelVoiceId))
        {
            if (!string.IsNullOrWhiteSpace(request.Voice)
                && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
            }

            if (!string.IsNullOrWhiteSpace(metadata?.VoiceId)
                && !string.Equals(metadata.VoiceId.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.voice_id", reason = "voice is derived from model id" });
            }
        }

        if (!string.IsNullOrWhiteSpace(modelVoiceProvider)
            && !string.IsNullOrWhiteSpace(metadata?.VoiceProvider)
            && !string.Equals(NormalizeVoiceProvider(metadata.VoiceProvider), modelVoiceProvider, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.voice_provider", reason = "voice provider is derived from model id" });
        }

        if (string.Equals(version, "2", StringComparison.Ordinal) && voice is null)
            throw new ArgumentException("HumeAI Octave version 2 requires a voice. Supply request.voice, providerOptions.humeai.voice_id, providerOptions.humeai.voice_name, or a voice shortcut model.", nameof(request));

        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language", reason = "HumeAI TTS language is voice-dependent" });

        if (metadata?.NumGenerations is > 1)
            warnings.Add(new { type = "partial", feature = "num_generations", reason = "Only the first generation is returned as the normalized audio response; all generation ids are included in provider metadata." });

        if (metadata?.InstantMode is true)
            warnings.Add(new { type = "ignored", feature = "providerOptions.humeai.instant_mode", reason = "instant_mode is only supported by HumeAI streaming TTS endpoints" });

        var utterance = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["description"] = description,
            ["speed"] = speed,
            ["trailing_silence"] = trailingSilence,
            ["voice"] = voice
        };

        var payload = new Dictionary<string, object?>
        {
            ["utterances"] = new[] { utterance },
            ["format"] = new Dictionary<string, object?> { ["type"] = outputFormat },
            ["version"] = version,
            ["temperature"] = metadata?.Temperature,
            ["num_generations"] = metadata?.NumGenerations,
            ["split_utterances"] = metadata?.SplitUtterances,
            ["strip_headers"] = metadata?.StripHeaders,
            ["include_timestamp_types"] = NormalizeTimestampTypes(metadata?.IncludeTimestampTypes),
            ["context"] = BuildContext(metadata)
        };

        if (metadata?.InstantMode is false)
            payload["instant_mode"] = false;

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v0/tts")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, HumeJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var requestId = HumeReadString(root, "request_id");

        if (!root.TryGetProperty("generations", out var generationsEl)
            || generationsEl.ValueKind != JsonValueKind.Array
            || generationsEl.GetArrayLength() == 0)
        {
            throw new InvalidOperationException($"{ProviderName} TTS response did not contain any generations.");
        }

        var firstGeneration = generationsEl.EnumerateArray().First();
        var audio = HumeReadString(firstGeneration, "audio");
        if (string.IsNullOrWhiteSpace(audio))
            throw new InvalidOperationException($"{ProviderName} TTS response did not contain generated audio.");

        var generationId = HumeReadString(firstGeneration, "generation_id");
        var generationIds = generationsEl.EnumerateArray()
            .Select(g => HumeReadString(g, "generation_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToArray();

        var providerMetadata = new Dictionary<string, JsonElement>
        {
            ["request_id"] = JsonSerializer.SerializeToElement(requestId, JsonSerializerOptions.Web),
            ["generation_id"] = JsonSerializer.SerializeToElement(generationId, JsonSerializerOptions.Web),
            ["generation_ids"] = JsonSerializer.SerializeToElement(generationIds, JsonSerializerOptions.Web),
            ["model"] = JsonSerializer.SerializeToElement(baseModelId, JsonSerializerOptions.Web),
            ["format"] = JsonSerializer.SerializeToElement(outputFormat, JsonSerializerOptions.Web)
        };

        if (voiceId is not null)
            providerMetadata["voice_id"] = JsonSerializer.SerializeToElement(voiceId, JsonSerializerOptions.Web);
        if (voiceName is not null)
            providerMetadata["voice_name"] = JsonSerializer.SerializeToElement(voiceName, JsonSerializerOptions.Web);
        if (voiceProvider is not null)
            providerMetadata["voice_provider"] = JsonSerializer.SerializeToElement(voiceProvider, JsonSerializerOptions.Web);
        if (firstGeneration.TryGetProperty("duration", out var durationEl))
            providerMetadata["duration"] = durationEl.Clone();
        if (firstGeneration.TryGetProperty("file_size", out var fileSizeEl))
            providerMetadata["file_size"] = fileSizeEl.Clone();
        if (firstGeneration.TryGetProperty("encoding", out var encodingEl))
            providerMetadata["encoding"] = encodingEl.Clone();

        return new SpeechResponse
        {
            ProviderMetadata = providerMetadata,
            Audio = new SpeechAudioResponse
            {
                Base64 = audio,
                MimeType = ResolveSpeechMimeType(outputFormat),
                Format = outputFormat
            },
            Warnings = warnings,
            Response = new ResponseData
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = new
                {
                    endpoint = "v0/tts",
                    status = (int)resp.StatusCode,
                    request_id = requestId,
                    generation_id = generationId,
                    generation_ids = generationIds,
                    content_type = resp.Content.Headers.ContentType?.MediaType
                }
            }
        };
    }

    private static (string BaseModelId, string? VoiceProvider, string? VoiceId) ParseSpeechModel(string model)
    {
        var raw = model.Trim();
        var providerPrefix = ProviderId + "/";
        if (raw.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            raw = raw[providerPrefix.Length..];

        if (string.IsNullOrWhiteSpace(raw))
            return (BaseSpeechModel, null, null);

        var parts = raw.Split('/', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
            return (parts[0], null, null);

        if (parts.Length == 2)
            return (parts[0], null, parts[1]);

        return (parts[0], NormalizeVoiceProvider(parts[1]), parts[2]);
    }

    private static string? NormalizeOutputFormat(string? outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat))
            return null;

        return outputFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" or "mpga" => "mp3",
            "wave" => "wav",
            var fmt when fmt is "mp3" or "wav" or "pcm" => fmt,
            _ => outputFormat.Trim().ToLowerInvariant()
        };
    }

    private static string? NormalizeOctaveVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return null;

        var normalized = version.Trim();
        return normalized switch
        {
            "1" or "2" => normalized,
            _ => throw new ArgumentException("HumeAI Octave version must be '1' or '2'.", nameof(version))
        };
    }

    private static string? NormalizeVoiceProvider(string? provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return null;

        return provider.Trim().Replace('-', '_').ToUpperInvariant() switch
        {
            "HUME" or "HUMEAI" or "HUME_AI" => "HUME_AI",
            "CUSTOM" or "CUSTOMVOICE" or "CUSTOM_VOICE" => "CUSTOM_VOICE",
            var p => p
        };
    }

    private static IReadOnlyList<string>? NormalizeTimestampTypes(IReadOnlyList<string>? timestampTypes)
    {
        if (timestampTypes is null || timestampTypes.Count == 0)
            return null;

        return [.. timestampTypes
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t is "word" or "phoneme")
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, object?>? BuildVoiceRef(string? voiceId, string? voiceName, string? voiceProvider)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var voice = new Dictionary<string, object?> { ["id"] = voiceId.Trim() };
            if (!string.IsNullOrWhiteSpace(voiceProvider))
                voice["provider"] = voiceProvider;
            return voice;
        }

        if (!string.IsNullOrWhiteSpace(voiceName))
        {
            var voice = new Dictionary<string, object?> { ["name"] = voiceName.Trim() };
            if (!string.IsNullOrWhiteSpace(voiceProvider))
                voice["provider"] = voiceProvider;
            return voice;
        }

        return null;
    }

    private static Dictionary<string, object?>? BuildContext(HumeAISpeechProviderMetadata? metadata)
    {
        if (!string.IsNullOrWhiteSpace(metadata?.ContextGenerationId))
            return new Dictionary<string, object?> { ["generation_id"] = metadata.ContextGenerationId.Trim() };

        if (metadata?.ContextUtterances is null || metadata.ContextUtterances.Count == 0)
            return null;

        var utterances = metadata.ContextUtterances
            .Where(u => !string.IsNullOrWhiteSpace(u.Text))
            .Select(u => new Dictionary<string, object?>
            {
                ["text"] = u.Text,
                ["description"] = u.Description,
                ["speed"] = u.Speed,
                ["trailing_silence"] = u.TrailingSilence,
                ["voice"] = BuildVoiceRef(u.VoiceId, u.VoiceName, NormalizeVoiceProvider(u.VoiceProvider))
            })
            .ToArray();

        return utterances.Length == 0
            ? null
            : new Dictionary<string, object?> { ["utterances"] = utterances };
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string ResolveSpeechMimeType(string outputFormat)
        => outputFormat.ToLowerInvariant() switch
        {
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            "pcm" => "audio/pcm",
            _ => "application/octet-stream"
        };
}
