using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Async;

public partial class AsyncProvider
{
    private static readonly byte[] AsyncSpeechQuotaExceededMarker = Encoding.ASCII.GetBytes("--ERROR:QUOTA_EXCEEDED--");

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
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

        if (!CanUseNativeAsyncSpeechStreaming(options.ResponseFormat))
        {
            var speechResponse = await SpeechRequest(options.ToSpeechRequest(), cancellationToken);
            foreach (var streamEvent in speechResponse.ToOpenAISpeechStreamEvents())
                yield return streamEvent;

            yield break;
        }

        ApplyAuthHeader();

        var body = BuildOpenAISpeechStreamingBody(options);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "text_to_speech/streaming")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"asyncAI streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024];

        while (!cancellationToken.IsCancellationRequested)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
                break;

            if (ContainsBytes(buffer.AsSpan(0, read), AsyncSpeechQuotaExceededMarker))
                throw new InvalidOperationException("asyncAI streaming TTS failed: quota exceeded during the audio stream.");

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

    private Dictionary<string, object?> BuildOpenAISpeechStreamingBody(AudioSpeechRequest options)
    {
        var (modelId, modelVoiceId) = ParseAsyncSpeechModelAndVoice(options.Model);
        var voiceId = (modelVoiceId ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("'voice' (async voice UUID) is required.", nameof(options));

        var outputFormat = BuildOpenAISpeechStreamingOutputFormat(options.ResponseFormat);

        var body = new Dictionary<string, object?>
        {
            ["model_id"] = modelId,
            ["transcript"] = options.Input,
            ["voice"] = new Dictionary<string, object?>
            {
                ["mode"] = "id",
                ["id"] = voiceId
            },
            ["output_format"] = outputFormat
        };

        if (options.Speed is not null)
            body["speed_control"] = options.Speed.Value;

        return body;
    }

    private static Dictionary<string, object?> BuildOpenAISpeechStreamingOutputFormat(string? responseFormat)
    {
        var container = ResolveAsyncStreamingContainer(responseFormat);
        var outputFormat = new Dictionary<string, object?>
        {
            ["container"] = container,
            ["sample_rate"] = 44100
        };

        if (container == "mp3")
        {
            outputFormat["bit_rate"] = 192000;
        }
        else
        {
            outputFormat["encoding"] = "pcm_s16le";
        }

        return outputFormat;
    }

    private static bool CanUseNativeAsyncSpeechStreaming(string? responseFormat)
        => ResolveOpenAISpeechResponseFormat(responseFormat) is "mp3" or "raw" or "pcm";

    private static string ResolveAsyncStreamingContainer(string? responseFormat)
        => ResolveOpenAISpeechResponseFormat(responseFormat) switch
        {
            "raw" or "pcm" => "raw",
            _ => "mp3"
        };

    private static string ResolveOpenAISpeechResponseFormat(string? responseFormat)
    {
        if (string.IsNullOrWhiteSpace(responseFormat))
            return "mp3";

        return responseFormat.Trim().ToLowerInvariant() switch
        {
            "mpeg" => "mp3",
            "wave" => "wav",
            _ => responseFormat.Trim().ToLowerInvariant()
        };
    }

    private static bool ContainsBytes(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty)
            return true;

        return haystack.IndexOf(needle) >= 0;
    }
}
