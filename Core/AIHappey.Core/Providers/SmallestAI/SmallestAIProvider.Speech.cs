using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.SmallestAI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider
{
    private static readonly JsonSerializerOptions SmallestAiSpeechJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const string TtsPath = "v1/tts";
    private const string TtsStreamPath = "v1/tts/live";

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var warnings = new List<object>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            warnings.Add(new { type = "unsupported", feature = "instructions" });

        var settings = PrepareTtsSynthesis(request, warnings);
        return await SynthesizeSpeechAsync(request, settings, warnings, cancellationToken);
    }

    private async Task<SpeechResponse> SynthesizeSpeechAsync(
        SpeechRequest request,
        TtsSynthesisSettings settings,
        List<object> warnings,
        CancellationToken cancellationToken)
    {
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, TtsPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildTtsPayload(request.Text, settings), SmallestAiSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        // SmallestAI requires this header even when the requested output format is not WAV.
        httpRequest.Headers.Accept.ParseAdd("audio/wav");

        using var resp = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken);
        if (!resp.IsSuccessStatusCode)
        {
            var errBody = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} TTS failed ({(int)resp.StatusCode}): {errBody}");
        }

        var audioBytes = await resp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audioBytes.Length == 0)
            throw new InvalidOperationException($"{ProviderName} TTS returned no audio bytes.");

        var mime = ResolveMimeType(settings.OutputFormat, settings.SampleRate);

        return new SpeechResponse
        {
            Audio = new SpeechAudioResponse
            {
                Base64 = Convert.ToBase64String(audioBytes),
                MimeType = mime,
                Format = settings.OutputFormat
            },
            Warnings = warnings,
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                [GetIdentifier()] = JsonSerializer.SerializeToElement(new
                {
                    model = settings.ModelId,
                    voiceId = settings.VoiceId,
                    sampleRate = settings.SampleRate,
                    speed = settings.Speed,
                    language = settings.Language,
                    outputFormat = settings.OutputFormat,
                    streamed = false
                })
            },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = resp.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = JsonSerializer.SerializeToElement(new
                {
                    model = settings.ModelId,
                    voiceId = settings.VoiceId,
                    sampleRate = settings.SampleRate,
                    speed = settings.Speed,
                    language = settings.Language,
                    outputFormat = settings.OutputFormat,
                    bytes = audioBytes.Length
                })
            }
        };
    }

    private TtsSynthesisSettings PrepareTtsSynthesis(SpeechRequest request, List<object>? warnings = null)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 8000)
            throw new ArgumentException("SmallestAI speech text must contain between 1 and 8000 characters after trimming.", nameof(request));
        var metadata = request.GetProviderMetadata<SmallestAISpeechProviderMetadata>(GetIdentifier());
        var (modelId, modelVoiceId) = ParseTtsModelAndVoiceFromModel(request.Model);
        var voiceId = modelVoiceId ?? request.Voice?.Trim();

        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException($"{ProviderName} voice is required. Use '{TtsModelPrefix}[model]/[voiceId]' or provide SpeechRequest.voice.", nameof(request));

        if (!string.IsNullOrWhiteSpace(modelVoiceId)
            && !string.IsNullOrWhiteSpace(request.Voice)
            && !string.Equals(request.Voice.Trim(), modelVoiceId, StringComparison.OrdinalIgnoreCase))
        {
            warnings?.Add(new { type = "ignored", feature = "voice", reason = "voice is derived from model id" });
        }

        var speed = request.Speed ?? 1f;
        if (!float.IsFinite(speed) || speed < 0.5f || speed > 2f)
            throw new ArgumentOutOfRangeException(nameof(request.Speed), "SmallestAI speed must be between 0.5 and 2.");

        var sampleRate = metadata?.SampleRate ?? 44100;
        if (sampleRate is not (8000 or 16000 or 24000 or 44100))
            throw new ArgumentOutOfRangeException(nameof(SmallestAISpeechProviderMetadata.SampleRate), "SmallestAI sampleRate must be one of: 8000, 16000, 24000, 44100.");

        var language = request.Language?.Trim() ?? metadata?.Language?.Trim();
        ValidateLanguage(language, nameof(request.Language));
        ValidateLanguage(metadata?.NumberPronunciationLanguage, nameof(SmallestAISpeechProviderMetadata.NumberPronunciationLanguage));

        return new TtsSynthesisSettings(
            modelId,
            voiceId,
            sampleRate,
            speed,
            language,
            NormalizeOutputFormat(request.OutputFormat ?? metadata?.OutputFormat),
            metadata?.PronunciationDicts,
            metadata?.NumberPronunciationLanguage?.Trim(),
            metadata?.SessionId?.Trim(),
            metadata?.RequestId?.Trim(),
            metadata?.WordTimestamps);
    }

    private static (string ModelId, string? VoiceId) ParseTtsModelAndVoiceFromModel(string model)
    {
        if (!model.StartsWith(TtsModelPrefix, StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException($"{ProviderName} model '{model}' is not supported. Expected '{TtsModelPrefix}[model]' or '{TtsModelPrefix}[model]/[voiceId]'.");

        var tail = model[TtsModelPrefix.Length..].Trim();
        var slashIndex = tail.LastIndexOf('/');
        var modelId = (slashIndex < 0 ? tail : tail[..slashIndex]).Trim().Replace('-', '_');
        var voiceId = slashIndex < 0 ? null : tail[(slashIndex + 1)..].Trim();

        if (modelId is not (LightningV31Model or LightningV31ProModel))
            throw new NotSupportedException($"{ProviderName} speech model '{model}' is not supported. Use '{LightningV31Model}' or '{LightningV31ProModel}'.");
        if (slashIndex >= 0 && string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice id cannot be empty after the model slash.", nameof(model));

        return (modelId, voiceId);
    }

    private static string NormalizeOutputFormat(string? outputFormat)
    {
        var normalized = outputFormat?.Trim().ToLowerInvariant();
        return normalized switch
        {
            "wave" => "wav",
            "mpeg" => "mp3",
            "mulaw" or "mu-law" or "u-law" => "ulaw",
            null or "" => "wav",
            "pcm" or "mp3" or "wav" or "ulaw" or "alaw" => normalized,
            _ => throw new ArgumentException("SmallestAI outputFormat must be one of: pcm, mp3, wav, ulaw, alaw.", nameof(outputFormat))
        };
    }

    private static Dictionary<string, object?> BuildTtsPayload(string text, TtsSynthesisSettings settings)
    {
        var payload = new Dictionary<string, object?>
        {
            ["text"] = text,
            ["voice_id"] = settings.VoiceId,
            ["model"] = settings.ModelId,
            ["sample_rate"] = settings.SampleRate,
            ["speed"] = settings.Speed,
            ["output_format"] = settings.OutputFormat,
            ["pronunciation_dicts"] = settings.PronunciationDicts,
            ["language"] = settings.Language,
            ["number_pronunciation_language"] = settings.NumberPronunciationLanguage,
            ["session_id"] = settings.SessionId,
            ["request_id"] = settings.RequestId,
            ["word_timestamps"] = settings.WordTimestamps
        };

        return payload;
    }

    private async IAsyncEnumerable<byte[]> StreamTtsAudioAsync(
        string text,
        TtsSynthesisSettings settings,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var httpRequest = CreateTtsStreamMessage(text, settings);
        using var response = await SendTtsStreamAsync(httpRequest, cancellationToken);
        await foreach (var item in ReadTtsSseAsync(response, cancellationToken))
            if (item.Audio.Length > 0)
                yield return item.Audio;
    }

    private static HttpRequestMessage CreateTtsStreamMessage(string text, TtsSynthesisSettings settings)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, TtsStreamPath)
        {
            Content = new StringContent(JsonSerializer.Serialize(BuildTtsPayload(text, settings), SmallestAiSpeechJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        message.Headers.Accept.ParseAdd("text/event-stream");
        return message;
    }

    private async Task<HttpResponseMessage> SendTtsStreamAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using (response)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new InvalidOperationException($"{ProviderName} TTS stream failed ({(int)response.StatusCode}): {error}");
            }
        }
        return response;
    }

    private static async IAsyncEnumerable<TtsSseEvent> ReadTtsSseAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var data = new StringBuilder();
        string? eventType = null;
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null || line.Length == 0)
            {
                if (data.Length > 0)
                {
                    var item = DecodeTtsSseEvent(data.ToString(), eventType);
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return item;
                    if (item.Done) yield break;
                }
                data.Clear();
                eventType = null;
                if (line is null) break;
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0) data.Append('\n');
                data.Append(line[5..].TrimStart(' '));
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
                eventType = line[6..].Trim();
        }
        throw new InvalidOperationException("SmallestAI TTS stream ended before its completion event.");
    }

    private static TtsSseEvent DecodeTtsSseEvent(string raw, string? eventType)
    {
        if (raw.Trim() == "[DONE]")
            return new(JsonSerializer.SerializeToElement(new { done = true }), [], true);
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("SmallestAI TTS stream payload must be an object.");
            var status = ReadString(root, "status");
            if (eventType == "error" || root.TryGetProperty("error", out _) || root.TryGetProperty("error_code", out _)
                || (status is not null && status is not ("200" or "206")))
                throw new InvalidOperationException($"SmallestAI TTS stream error: {raw}");
            var done = root.TryGetProperty("done", out var doneElement) && doneElement.ValueKind == JsonValueKind.True;
            var audio = root.TryGetProperty("audio", out var audioElement) && audioElement.ValueKind == JsonValueKind.String
                ? Convert.FromBase64String(audioElement.GetString()!) : [];
            if (!done && audio.Length == 0)
                throw new InvalidOperationException("SmallestAI TTS stream event did not contain audio or completion.");
            return new(root.Clone(), audio, done);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw new InvalidOperationException("SmallestAI TTS stream contained malformed JSON or base64 audio.", exception);
        }
    }

    private sealed record TtsSseEvent(JsonElement Payload, byte[] Audio, bool Done);

    private static void ValidateLanguage(string? language, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(language))
            return;

        string[] supported = ["auto", "en", "hi", "mr", "kn", "ta", "bn", "gu", "te", "ml", "pa", "or", "es", "de", "fr", "it", "nl", "sv", "pt", "ru", "el", "fi", "no", "pl", "ar", "zh", "id", "ja", "ko", "ms", "tr", "vi"];
        if (!supported.Contains(language.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException($"SmallestAI language '{language}' is not supported.", parameterName);
    }

    private static string ResolveMimeType(string outputFormat, int sampleRate)
        => outputFormat switch
        {
            "wav" => "audio/wav",
            "mp3" => "audio/mpeg",
            "ulaw" => "audio/basic",
            "alaw" => "audio/basic",
            "pcm" => $"audio/L16;rate={sampleRate}",
            _ => "audio/wav"
        };

    private sealed record TtsSynthesisSettings(
        string ModelId,
        string VoiceId,
        int SampleRate,
        float Speed,
        string? Language,
        string OutputFormat,
        IReadOnlyList<string>? PronunciationDicts,
        string? NumberPronunciationLanguage,
        string? SessionId,
        string? RequestId,
        bool? WordTimestamps);
}

