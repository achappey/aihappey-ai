using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.FishAudio;

public partial class FishAudioProvider
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
        var model = NormalizeModel(options.Model);

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v1/tts/stream/with-timestamp")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJson),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Add("model", model);
        httpRequest.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"FishAudio streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var data = line["data:".Length..].Trim();
            if (string.IsNullOrWhiteSpace(data))
                continue;

            if (string.Equals(data, "[DONE]", StringComparison.OrdinalIgnoreCase))
                break;

            var audioBase64 = ExtractFishAudioStreamAudio(data);
            if (string.IsNullOrWhiteSpace(audioBase64))
                continue;

            yield return new AudioSpeechStreamDelta
            {
                Audio = audioBase64
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
        var referenceId = ResolveOpenAISpeechReferenceId(options);
        if (string.IsNullOrWhiteSpace(referenceId))
            throw new ArgumentException("FishAudio voice is required. Provide AudioSpeechRequest.voice with a FishAudio reference_id.", nameof(options));

        var payload = new Dictionary<string, object?>
        {
            ["text"] = options.Input,
            ["reference_id"] = referenceId,
            ["format"] = NormalizeSpeechFormat(options.ResponseFormat),
            ["latency"] = "balanced"
        };

        if (options.Speed is not null)
        {
            payload["prosody"] = new Dictionary<string, object?>
            {
                ["speed"] = options.Speed.Value
            };
        }

        return payload;
    }

    private static string? ResolveOpenAISpeechReferenceId(AudioSpeechRequest options)
        => string.IsNullOrWhiteSpace(options.Voice) ? null : options.Voice.Trim();

    private static string? ExtractFishAudioStreamAudio(string data)
    {
        try
        {
            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;

            if (!root.TryGetProperty("audio_base64", out var audioElement)
                || audioElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return audioElement.GetString();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse FishAudio speech SSE json event: {data}", ex);
        }
    }

}

