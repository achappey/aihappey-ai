using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider
{
    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = PrepareInworldOpenAISpeech(options, streaming: false);
        if (options.AdditionalProperties is { Count: > 0 })
        {
            var native = InworldNativeOpenAISpeech(options);
            var response = await SpeechRequest(native, cancellationToken);
            return response.ToOpenAISpeechAudio();
        }
        using var message = InworldJsonRequest("v1/audio/speech", payload);
        message.Headers.Accept.Clear();
        ApplyAuthHeader();
        using var upstream = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureInworldSuccessAsync(upstream, "OpenAI-compatible TTS", cancellationToken);
        var audio = await upstream.Content.ReadAsByteArrayAsync(cancellationToken);
        if (audio.Length == 0) throw new InvalidOperationException("Inworld TTS returned empty audio.");
        var encoding = NormalizeAudioEncoding(options.ResponseFormat) ?? "MP3";
        return (audio, upstream.Content.Headers.ContentType?.MediaType ?? MapEncodingToMimeType(encoding));
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = PrepareInworldOpenAISpeech(options, streaming: true);
        if (options.AdditionalProperties is { Count: > 0 })
        {
            var native = InworldNativeOpenAISpeech(options);
            await foreach (var part in SpeechStreamingAsync(new StreamingSpeechRequest
            {
                Model = native.Model, Text = [new() { Text = native.Text }], Voice = native.Voice,
                OutputFormat = new() { Type = native.OutputFormat ?? "mp3" }, Instructions = native.Instructions,
                Speed = native.Speed, ProviderOptions = native.ProviderOptions
            }, cancellationToken))
            {
                if (part is SpeechAudioPart { Audio.Length: > 0 } audio) yield return new AudioSpeechStreamDelta { Audio = audio.Audio };
                else if (part is SpeechFinishPart) yield return new AudioSpeechStreamDone();
            }
            yield break;
        }
        using var message = InworldJsonRequest("v1/audio/speech", payload);
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        ApplyAuthHeader();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureInworldSuccessAsync(response, "OpenAI-compatible streaming TTS", cancellationToken);
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidOperationException("Inworld streaming TTS did not return SSE.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var hasAudio = false;
        await foreach (var item in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.Data) || item.Data == "[DONE]") continue;
            using var document = JsonDocument.Parse(item.Data);
            var root = document.RootElement;
            ThrowInworldPayloadError(root);
            switch (RequiredInworldString(root, "type"))
            {
                case "speech.audio.delta":
                    var audio = RequiredInworldString(root, "audio");
                    _ = Convert.FromBase64String(audio);
                    hasAudio = true;
                    yield return new AudioSpeechStreamDelta { Audio = audio };
                    break;
                case "speech.audio.done":
                    if (!hasAudio) throw new InvalidOperationException("Inworld streaming TTS completed without audio.");
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return new AudioSpeechStreamDone();
                    yield break;
                case "error":
                    throw new InvalidOperationException($"Inworld streaming TTS failed: {root.GetRawText()}");
                default:
                    throw new InvalidOperationException("Inworld streaming TTS returned an unexpected event.");
            }
        }
        throw new InvalidOperationException("Inworld streaming TTS ended before speech.audio.done.");
    }

    private static JsonObject PrepareInworldOpenAISpeech(AudioSpeechRequest options, bool streaming)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        var (model, shortcutVoice) = ParseInworldSpeechModelAndVoice(options.Model);
        var voice = shortcutVoice ?? options.Voice;
        if (string.IsNullOrWhiteSpace(voice) && !(options.AdditionalProperties?.ContainsKey("voiceDesign") == true))
            throw new ArgumentException("Inworld TTS requires an Inworld voice ID.");
        if (options.Input.Length > 4000) throw new ArgumentException("Inworld OpenAI-compatible TTS accepts at most 4,000 UTF-16 code units.");
        var format = options.ResponseFormat?.Trim().ToLowerInvariant() ?? "mp3";
        if (format is not ("mp3" or "opus" or "flac" or "wav" or "pcm")) throw new NotSupportedException($"Inworld does not support OpenAI speech format '{format}'.");
        if (options.Speed is { } speed && (!double.IsFinite(speed) || speed < 0.5 || speed > 1.5)) throw new ArgumentOutOfRangeException(nameof(options.Speed));
        if (options.StreamFormat is not null && options.StreamFormat != (streaming ? "sse" : "audio"))
            throw new NotSupportedException(streaming ? "Speech events require stream_format 'sse'." : "Binary speech requires stream_format 'audio'.");
        var payload = new JsonObject { ["model"] = model, ["input"] = options.Input, ["voice"] = voice, ["response_format"] = format, ["stream_format"] = streaming ? "sse" : "audio" };
        if (options.Speed is not null) payload["speed"] = options.Speed.Value;
        if (options.Instructions is not null) payload["instructions"] = options.Instructions;
        return payload;
    }

    private static SpeechRequest InworldNativeOpenAISpeech(AudioSpeechRequest options)
    {
        // Explicit native fields opt into the native API; never silently drop extension data.
        foreach (var key in options.AdditionalProperties!.Keys)
            if (!InworldSpeechOptions.Contains(key)) throw new ArgumentException($"Unsupported Inworld native speech option '{key}'.");
        var request = options.ToSpeechRequest();
        request.ProviderOptions = new() { ["inworld"] = JsonSerializer.SerializeToElement(options.AdditionalProperties) };
        // Match OpenAI-compatible headerless PCM's documented 24 kHz default.
        if (options.ResponseFormat == "pcm" && !options.AdditionalProperties.ContainsKey("audioConfig"))
        {
            var native = JsonSerializer.SerializeToNode(options.AdditionalProperties)!.AsObject();
            native["audioConfig"] = new JsonObject { ["sampleRateHertz"] = 24000 };
            request.ProviderOptions["inworld"] = JsonSerializer.SerializeToElement(native);
        }
        return request;
    }
}
