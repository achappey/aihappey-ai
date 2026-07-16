using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Speechify;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Speechify;

public partial class SpeechifyProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        var response = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateOpenAISpeechRequest(options);

        // Speechify streaming does not support wav containers. Preserve OpenAI compatibility
        // by fulfilling wav requests through the existing complete speech endpoint and exposing
        // the resulting audio as one synthetic stream delta.
        if (IsOpenAIWavResponseFormat(options.ResponseFormat))
        {
            var speechResponse = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
            foreach (var streamEvent in speechResponse.ToOpenAISpeechStreamEvents())
                yield return streamEvent;

            yield break;
        }

        ApplyAuthHeader();

        var (payload, acceptMimeType) = BuildOpenAISpeechStreamingRequest(options);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/audio/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        if (!string.IsNullOrWhiteSpace(acceptMimeType))
            httpRequest.Headers.Accept.ParseAdd(acceptMimeType);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Speechify streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            yield return new AudioSpeechStreamDelta
            {
                Audio = Convert.ToBase64String(buffer.AsSpan(0, read))
            };
        }

        yield return new AudioSpeechStreamDone();
    }

    private static void ValidateOpenAISpeechRequest(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Model))
            throw new ArgumentException("Model is required.", nameof(options));

        if (string.IsNullOrWhiteSpace(options.Input))
            throw new ArgumentException("Input is required.", nameof(options));
    }

    private (Dictionary<string, object?> Payload, string? AcceptMimeType) BuildOpenAISpeechStreamingRequest(AudioSpeechRequest options)
    {
        var (model, modelVoiceId) = ResolveModelAndVoice(options.Model);
        var voiceId = (modelVoiceId ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Speechify requires a voice_id. Provide a Speechify voice shortcut in the model id or AudioSpeechRequest.voice.", nameof(options));

        var payload = new Dictionary<string, object?>
        {
            ["input"] = options.Input,
            ["voice_id"] = voiceId,
            ["model"] = model
        };

        var outputFormat = NormalizeSpeechifyStreamingOutputFormat(options.ResponseFormat);
        if (!string.IsNullOrWhiteSpace(outputFormat))
        {
            payload["output_format"] = outputFormat;
            return (payload, null);
        }

        return (payload, ResolveSpeechifyStreamingAcceptMimeType(options.ResponseFormat));
    }

    private static bool IsOpenAIWavResponseFormat(string? responseFormat)
        => string.Equals(
            NormalizeSpeechifyOpenAIResponseFormat(responseFormat),
            "wav",
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSpeechifyOpenAIResponseFormat(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return null;

        var fmt = responseFormat.Trim().ToLowerInvariant();
        return fmt switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            "mulaw" or "mu-law" or "ulaw" or "u-law" => "ulaw",
            _ => fmt
        };
    }

    private static string? NormalizeSpeechifyStreamingOutputFormat(string? responseFormat)
    {
        var fmt = NormalizeSpeechifyOpenAIResponseFormat(responseFormat);
        return fmt switch
        {
            null => null,
            "mp3" => "mp3_24000_128",
            "aac" => "aac_24000",
            "ogg" or "opus" => "ogg_24000",
            "pcm" => "pcm_24000",
            "ulaw" => "ulaw_8000",
            _ when IsSpeechifyExplicitStreamingOutputFormat(fmt) => fmt,
            _ => null
        };
    }

    private static bool IsSpeechifyExplicitStreamingOutputFormat(string value)
        => value.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase)
           || value.StartsWith("mp3_", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "ulaw_8000", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "ogg_24000", StringComparison.OrdinalIgnoreCase)
           || string.Equals(value, "aac_24000", StringComparison.OrdinalIgnoreCase);

    private static string ResolveSpeechifyStreamingAcceptMimeType(string? responseFormat)
    {
        var fmt = NormalizeSpeechifyOpenAIResponseFormat(responseFormat);
        return fmt switch
        {
            "mp3" => "audio/mpeg",
            "aac" => "audio/aac",
            "ogg" or "opus" => "audio/ogg",
            "pcm" => "audio/pcm",
            _ => "audio/mpeg"
        };
    }
}
