using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.Inworld;
using AIHappey.Core.AI;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    private static readonly JsonSerializerOptions InworldSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));

        var voiceId = request.Voice?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Inworld TTS requires a voiceId. Provide SpeechRequest.voice.", nameof(request));

        var now = DateTime.UtcNow;
        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (!string.IsNullOrWhiteSpace(request.Language))
            warnings.Add(new { type = "unsupported", feature = "language" });

        var metadata = request.GetProviderMetadata<InworldSpeechProviderMetadata>(GetIdentifier());

        var modelId = NormalizeInworldModelId(request.Model);
        var audioConfig = BuildAudioConfig(request, metadata);

        var payload = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["voiceId"] = voiceId,
            ["modelId"] = modelId,
            ["audioConfig"] = audioConfig,
            ["timestampType"] = metadata?.TimestampType,
            ["applyTextNormalization"] = metadata?.ApplyTextNormalization
        };

        if (metadata?.AudioConfig?.BitRate is { } bitRate && bitRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(InworldSpeechAudioConfig.BitRate), "bitRate must be > 0.");
        if (metadata?.AudioConfig?.SampleRateHertz is { } sampleRate && (sampleRate < 8000 || sampleRate > 48000))
            throw new ArgumentOutOfRangeException(nameof(InworldSpeechAudioConfig.SampleRateHertz), "sampleRateHertz must be between 8000 and 48000.");

        var speakingRate = request.Speed ?? metadata?.AudioConfig?.SpeakingRate;
        if (speakingRate is { } sr && (sr < 0.5 || sr > 1.5))
            throw new ArgumentOutOfRangeException(nameof(InworldSpeechAudioConfig.SpeakingRate), "speakingRate must be between 0.5 and 1.5.");

        if (metadata?.AudioConfig?.SpeakingRate is not null && request.Speed is not null)
            warnings.Add(new { type = "override", feature = "speakingRate", detail = "SpeechRequest.speed overrides providerOptions.inworld.audioConfig.speakingRate" });

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "tts/v1/voice")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, InworldSpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Inworld TTS failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var audioBase64 = root.TryGetProperty("audioContent", out var audioEl) && audioEl.ValueKind == JsonValueKind.String
            ? audioEl.GetString()
            : null;

        if (string.IsNullOrWhiteSpace(audioBase64))
            throw new InvalidOperationException("Inworld TTS response missing audioContent.");

        var encoding = ResolveAudioEncoding(request, metadata);
        var format = MapEncodingToFormat(encoding);
        var mime = MapEncodingToMimeType(encoding);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = audioBase64,
                MimeType = mime,
                Format = format
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = root.Clone()
            },
            Response = new()
            {
                Timestamp = now,
                ModelId = request.Model,
                Body = root.Clone()
            }
        };
    }

    private static string NormalizeInworldModelId(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model is required.", nameof(model));

        // Accept both "inworld/<model>" and "<model>".
        var prefix = "inworld/";
        return model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? model.SplitModelId().Model
            : model.Trim();
    }

    private static object? BuildAudioConfig(SpeechRequest request, InworldSpeechProviderMetadata? metadata)
    {
        var audioEncoding = ResolveAudioEncoding(request, metadata);
        var bitRate = metadata?.AudioConfig?.BitRate;
        var sampleRate = metadata?.AudioConfig?.SampleRateHertz;
        var speakingRate = request.Speed ?? metadata?.AudioConfig?.SpeakingRate;

        if (string.IsNullOrWhiteSpace(audioEncoding) && bitRate is null && sampleRate is null && speakingRate is null)
            return null;

        return new Dictionary<string, object?>
        {
            ["audioEncoding"] = audioEncoding,
            ["bitRate"] = bitRate,
            ["sampleRateHertz"] = sampleRate,
            ["speakingRate"] = speakingRate
        };
    }

    private static string? ResolveAudioEncoding(SpeechRequest request, InworldSpeechProviderMetadata? metadata)
    {
        var fromRequest = NormalizeAudioEncoding(request.OutputFormat);
        if (!string.IsNullOrWhiteSpace(fromRequest))
            return fromRequest;

        var fromMetadata = NormalizeAudioEncoding(metadata?.AudioConfig?.AudioEncoding);
        return !string.IsNullOrWhiteSpace(fromMetadata) ? fromMetadata : null;
    }

    private static string? NormalizeAudioEncoding(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var v = value.Trim().ToUpperInvariant();

        return v switch
        {
            "MP3" or "MPEG" => "MP3",
            "OGG_OPUS" or "OPUS" or "OGG" => "OGG_OPUS",
            "LINEAR16" or "PCM" or "WAV" => "LINEAR16",
            "FLAC" => "FLAC",
            "ALAW" => "ALAW",
            "MULAW" or "MU_LAW" or "MU-LAW" => "MULAW",
            _ => v
        };
    }

    private static string MapEncodingToFormat(string? audioEncoding)
    {
        var encoding = (audioEncoding ?? "MP3").Trim().ToUpperInvariant();
        return encoding switch
        {
            "OGG_OPUS" => "opus",
            "LINEAR16" => "wav",
            "FLAC" => "flac",
            "ALAW" => "alaw",
            "MULAW" => "mulaw",
            _ => "mp3"
        };
    }

    private static string MapEncodingToMimeType(string? audioEncoding)
    {
        var encoding = (audioEncoding ?? "MP3").Trim().ToUpperInvariant();
        return encoding switch
        {
            "OGG_OPUS" => "audio/ogg",
            "LINEAR16" => "audio/wav",
            "FLAC" => "audio/flac",
            "ALAW" => "audio/alaw",
            "MULAW" => "audio/mulaw",
            _ => "audio/mpeg"
        };
    }
}
