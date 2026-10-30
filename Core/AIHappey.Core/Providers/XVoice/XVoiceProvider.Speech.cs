using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.XVoice;

public partial class XVoiceProvider
{
    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SpeechPayload(request);
        using var message = SpeechMessage(payload);
        ApplyAuthHeader();
        using var response = await SendAsync(message, cancellationToken);
        var format = SpeechString(payload, "response_format")!;
        using var buffer = new MemoryStream();
        JsonElement? usage = null;
        await foreach (var chunk in ReadSpeechAsync(response, payload, cancellationToken))
        {
            if (chunk.Audio is { } data)
                await buffer.WriteAsync(Convert.FromBase64String(data), cancellationToken);
            usage = chunk.Usage ?? usage;
        }
        var audio = buffer.ToArray();
        var mime = response.Content.Headers.ContentType?.MediaType;
        return new SpeechResponse
        {
            Audio = new()
            {
                Base64 = Convert.ToBase64String(audio), Format = format,
                MimeType = mime?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true ? mime : SpeechMime(format)
            },
            Warnings = SpeechWarnings(payload),
            ProviderMetadata = new() { [ProviderId] = JsonSerializer.SerializeToElement(new { usage }) },
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow, ModelId = request.Model.ToModelId(ProviderId),
                Headers = response.GetHeaders()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        var response = await SpeechRequest(ToSpeech(options), cancellationToken);
        return response.ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = SpeechPayload(ToSpeech(options), streaming: true);
        ApplyAuthHeader();
        using var message = SpeechMessage(payload);
        using var response = await SendAsync(message, cancellationToken);
        await foreach (var chunk in ReadSpeechAsync(response, payload, cancellationToken))
        {
            if (chunk.Audio is { } audio)
                yield return new AudioSpeechStreamDelta { Audio = audio };
            if (chunk.Done)
                yield return new AudioSpeechStreamDone
                {
                    Usage = chunk.Usage is { ValueKind: JsonValueKind.Object } usage
                        ? usage.Deserialize<AudioSpeechUsage>() : null
                };
        }
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (request.Text is null || request.Text.Length == 0
            || request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("xVoice speech requires non-null text parts.", nameof(request));
        if (request.OutputFormat?.Rate is { } rate && rate != 24000)
            throw new NotSupportedException("xVoice speech output uses 24000 Hz.");
        if (request.Speed is { } speed && (!double.IsFinite(speed) || speed != 1))
            throw new ArgumentException("xVoice streaming speech speed must be 1.0.", nameof(request));
        var text = string.Concat(request.Text.Select(p => p.Text));
        Dictionary<string, object?>? payload = null;
        foreach (var part in request.Text)
        {
            var native = TranscriptionOptions(request.ProviderOptions);
            foreach (var option in TranscriptionOptions(part.ProviderOptions))
                native[option.Key] = option.Value;
            var current = SpeechPayload(new SpeechRequest
            {
                Model = request.Model, Text = text, Voice = part.Voice ?? request.Voice,
                Speed = (float?)request.Speed, Language = request.Language,
                Instructions = request.Instructions, OutputFormat = request.OutputFormat?.Type,
                ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(native) }
            }, streaming: true);
            if (payload is not null && !JsonElement.DeepEquals(
                JsonSerializer.SerializeToElement(payload), JsonSerializer.SerializeToElement(current)))
                throw new NotSupportedException("xVoice accepts one effective voice and configuration per speech request.");
            payload ??= current;
        }
        ApplyAuthHeader();
        using var message = SpeechMessage(payload!);
        using var response = await SendAsync(message, cancellationToken);
        yield return new SpeechStreamStartPart { Warnings = SpeechWarnings(payload!) };
        yield return new SpeechResponseMetadataPart
        {
            ModelId = request.Model.ToModelId(ProviderId), Timestamp = DateTime.UtcNow, Headers = response.GetHeaders()
        };
        await foreach (var chunk in ReadSpeechAsync(response, payload!, cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = chunk.Raw };
            if (chunk.Audio is { } audio)
                yield return new SpeechAudioPart
                {
                    Audio = audio,
                    AudioFormat = new() { Type = SpeechMime(chunk.Format), Rate = 24000 }
                };
            if (chunk.Done)
                yield return new SpeechFinishPart
                {
                    Usage = chunk.Usage is { ValueKind: JsonValueKind.Object } usage ? SpeechUsage(usage) : null,
                    ProviderMetadata = chunk.Usage is { } raw ? new() { [ProviderId] = raw } : null
                };
        }
    }

    private static SpeechRequest ToSpeech(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var native = new Dictionary<string, JsonElement>();
        if (options.AdditionalProperties is { } additional)
        {
            if (additional.TryGetValue("providerOptions", out var providers)
                && providers.TryGetProperty(ProviderId, out var provider))
                native = TranscriptionOptions(new() { [ProviderId] = provider });
            foreach (var field in additional.Where(p => p.Key != "providerOptions"))
                native[field.Key] = field.Value.Clone();
        }
        if (options.StreamFormat is not null)
            native["stream_format"] = JsonSerializer.SerializeToElement(options.StreamFormat);
        return new SpeechRequest
        {
            Model = options.Model, Text = options.Input, Voice = options.Voice,
            OutputFormat = options.ResponseFormat, Speed = options.Speed, Instructions = options.Instructions,
            ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(native) }
        };
    }

    private static Dictionary<string, object?> SpeechPayload(SpeechRequest request, bool streaming = false)
    {
        var payload = TranscriptionOptions(request.ProviderOptions).ToDictionary(p => p.Key, p => (object?)p.Value);
        var (model, voice) = ParseSpeechModel(request.Model);
        payload["model"] = model;
        payload["input"] = request.Text;
        if ((voice ?? request.Voice) is { } selected) payload["voice"] = selected;
        if (request.Language is not null) payload["language"] = request.Language;
        if (request.Instructions is not null) payload["instructions"] = request.Instructions;
        if (request.Speed is not null) payload["speed"] = request.Speed;
        if (request.OutputFormat is not null) payload["response_format"] = NormalizeSpeechFormat(request.OutputFormat);
        payload.TryAdd("response_format", "wav");
        var format = SpeechString(payload, "response_format");
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.EnumerateRunes().Count() > 8000)
            throw new ArgumentException("xVoice input must contain between 1 and 8000 characters.", nameof(request));
        if (string.IsNullOrWhiteSpace(SpeechString(payload, "voice")))
            throw new ArgumentException("xVoice requires a voice.", nameof(request));
        if (SpeechString(payload, "instructions") is { } instructions && instructions.EnumerateRunes().Count() > 1000)
            throw new ArgumentException("xVoice instructions are limited to 1000 characters.", nameof(request));
        var streamFormat = SpeechString(payload, "stream_format");
        if (streamFormat is not (null or "audio" or "sse"))
            throw new ArgumentException("xVoice stream_format must be audio or sse.");
        if (streaming)
        {
            payload["stream"] = true;
            payload.TryAdd("stream_format", "sse");
        }
        else if (streamFormat is not null)
            payload["stream"] = true;
        if (payload.TryGetValue("speed", out var value))
        {
            var speed = JsonSerializer.SerializeToElement(value).GetDouble();
            if (!double.IsFinite(speed) || speed <= 0
                || (IsSpeechStreaming(payload) && speed != 1))
                throw new ArgumentException("xVoice speed must be positive and 1.0 while streaming.");
        }
        return payload;
    }

    private static bool IsSpeechStreaming(Dictionary<string, object?> payload)
        => payload.TryGetValue("stream", out var stream) && JsonSerializer.SerializeToElement(stream).GetBoolean();

    private static string? SpeechString(Dictionary<string, object?> payload, string key)
        => payload.TryGetValue(key, out var value) ? JsonSerializer.SerializeToElement(value).GetString() : null;

    private static string NormalizeSpeechFormat(string value) => value.ToLowerInvariant() switch
    {
        "audio/wav" or "audio/x-wav" => "wav",
        "audio/mpeg" or "audio/mp3" => "mp3",
        "audio/pcm" => "pcm",
        var format => format
    };

    private static string SpeechMime(string format) => format switch
    {
        "mp3" => "audio/mpeg",
        _ => "audio/" + format
    };

    private static IEnumerable<object> SpeechWarnings(Dictionary<string, object?> payload)
        => SpeechString(payload, "model") == "cadence-tts" && SpeechString(payload, "instructions") is { Length: > 0 }
            ? [new { type = "unsupported", feature = "instructions", details = "Accepted but ignored by cadence-tts." }] : [];

    private static HttpRequestMessage SpeechMessage(Dictionary<string, object?> payload)
        => new(HttpMethod.Post, "v1/audio/speech") { Content = JsonContent.Create(payload) };

    private sealed record SpeechChunk(string? Audio, string Format, bool Done, JsonElement? Usage, object Raw);

    private static async IAsyncEnumerable<SpeechChunk> ReadSpeechAsync(
        HttpResponseMessage response, Dictionary<string, object?> payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var format = SpeechString(payload, "response_format")!;
        var hasAudio = false;
        if (SpeechString(payload, "stream_format") == "sse")
        {
            await foreach (var item in ReadEventsAsync(response, "speech.audio.done", cancellationToken))
            {
                var type = TranscriptionString(item, "type");
                if (type == "speech.audio.delta")
                {
                    var audio = item.GetProperty("audio").GetString()!;
                    if (Convert.FromBase64String(audio).Length == 0)
                        continue;
                    hasAudio = true;
                    yield return new(audio, TranscriptionString(item, "response_format") ?? format, false, null, item);
                }
                else if (type == "speech.audio.done")
                {
                    if (!hasAudio)
                        throw new InvalidOperationException("xVoice returned no audio (model_error).");
                    yield return new(null, format, true, item.TryGetProperty("usage", out var usage) ? usage.Clone() : null, item);
                }
            }
        }
        else
        {
            var mime = response.Content.Headers.ContentType?.MediaType;
            if (mime is not null && mime != "application/octet-stream" && !mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"xVoice returned '{mime}' when binary audio was requested.");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16384];
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                hasAudio = true;
                var audio = Convert.ToBase64String(buffer, 0, read);
                yield return new(audio, format, false, null, audio);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasAudio)
                throw new InvalidOperationException("xVoice returned no audio (model_error).");
            yield return new(null, format, true, null, new { type = "speech.audio.done" });
        }
    }

    private static StreamingSpeechUsage SpeechUsage(JsonElement usage)
        => new()
        {
            InputCharacters = usage.TryGetProperty("input_characters", out var characters) ? characters.GetInt64()
                : usage.TryGetProperty("characters", out characters) ? characters.GetInt64() : null,
            InputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : null,
            OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt64() : null,
            Raw = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
        };
}
