using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider
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
        ApplyAuthHeader();

        var payload = BuildOpenAISpeechStreamingPayload(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/speech/stream")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, MurfJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        using var response = await _client.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(
                $"MurfAI streaming TTS failed ({(int)response.StatusCode}): {error}");
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

    private static Dictionary<string, object?> BuildOpenAISpeechStreamingPayload(AudioSpeechRequest options)
    {
        var (modelVersionFromModel, voiceIdFromModel) = ParseSpeechModelAndVoice(options.Model);
        var voiceId = (voiceIdFromModel ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            throw new ArgumentException(
                "MurfAI requires a voiceId. Provide a MurfAI voice shortcut model or AudioSpeechRequest.voice.",
                nameof(options));
        }

        var model = (modelVersionFromModel ?? "gen2").Trim();
        var format = NormalizeMurfFormat(options.ResponseFormat) ?? "mp3";
        var payload = new Dictionary<string, object?>
        {
            ["text"] = options.Input,
            ["voiceId"] = voiceId,
            ["model"] = model,
            ["format"] = format.ToUpperInvariant()
        };

        if (options.Speed is not null)
        {
            var rate = (int)Math.Round(
                (options.Speed.Value - 1.0f) * 50.0f,
                MidpointRounding.AwayFromZero);
            payload["rate"] = Math.Clamp(rate, -50, 50);
        }

        return payload;
    }
}

