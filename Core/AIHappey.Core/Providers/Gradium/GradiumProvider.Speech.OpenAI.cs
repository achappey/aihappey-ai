using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider
{
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
        ApplyAuthHeader();

        var payload = BuildOpenAISpeechStreamingPayload(options);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/post/speech/tts")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, SpeechJsonOptions),
                Encoding.UTF8,
                MediaTypeNames.Application.Json)
        };

        httpRequest.Headers.Accept.ParseAdd("application/x-ndjson");
        httpRequest.Headers.Accept.ParseAdd(MediaTypeNames.Application.Json);

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"{ProviderName} streaming TTS failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var sawDone = false;
        string? line;
        while (!cancellationToken.IsCancellationRequested
               && (line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            using var document = ParseGradiumSpeechStreamJson(line);
            var root = document.RootElement;
            var eventType = root.TryGetString("type");

            switch (eventType)
            {
                case "audio":
                    var audio = root.TryGetString("audio");
                    if (!string.IsNullOrWhiteSpace(audio))
                    {
                        yield return new AudioSpeechStreamDelta
                        {
                            Audio = NormalizeGradiumSpeechStreamAudio(audio)
                        };
                    }

                    break;

                case "end_of_stream":
                    sawDone = true;
                    yield return new AudioSpeechStreamDone();
                    yield break;

                case "ready":
                case "text":
                case "flushed":
                    break;

                case "error":
                    throw new InvalidOperationException($"{ProviderName} streaming TTS failed: {ReadGradiumSpeechStreamError(root)}");

                default:
                    if (!string.IsNullOrWhiteSpace(eventType))
                        throw new InvalidOperationException($"Unsupported {ProviderName} speech stream event type '{eventType}'.");

                    throw new InvalidOperationException($"{ProviderName} speech stream event did not include a type: {line}");
            }
        }

        if (!sawDone)
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
        var (baseModelId, modelVoiceId) = ParseModelAndVoice(options.Model);
        var voiceId = (modelVoiceId ?? options.Voice)?.Trim();
        if (string.IsNullOrWhiteSpace(voiceId))
            throw new ArgumentException("Voice is required for Gradium speech requests.", nameof(options));

        var outputFormat = NormalizeOutputFormat(options.ResponseFormat) ?? "wav";
        var payload = new Dictionary<string, object?>
        {
            ["text"] = options.Input,
            ["voice_id"] = voiceId,
            ["output_format"] = outputFormat,
            ["only_audio"] = false
        };

        if (!string.Equals(baseModelId, BaseSpeechModel, StringComparison.OrdinalIgnoreCase))
            payload["model_name"] = baseModelId;

        return payload;
    }

    private static JsonDocument ParseGradiumSpeechStreamJson(string line)
    {
        try
        {
            return JsonDocument.Parse(line);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Failed to parse {ProviderName} speech stream event: {line}", ex);
        }
    }

    private static string NormalizeGradiumSpeechStreamAudio(string audio)
    {
        try
        {
            return Convert.ToBase64String(Convert.FromBase64String(audio));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"{ProviderName} speech stream returned an invalid base64 audio chunk.", ex);
        }
    }

    private static string ReadGradiumSpeechStreamError(JsonElement root)
    {
        var message = root.TryGetString("message")
                      ?? root.TryGetString("error")
                      ?? root.GetRawText();

        var code = root.TryGetProperty("code", out var codeElement)
            ? codeElement.ToString()
            : null;

        return string.IsNullOrWhiteSpace(code)
            ? message
            : $"{code}: {message}";
    }
}
