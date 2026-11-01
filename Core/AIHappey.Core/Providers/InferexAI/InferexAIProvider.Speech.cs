using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider
{
    private static Dictionary<string, object?> SpeechPayload(SpeechRequest request, bool streaming, double? rate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var payload = Options(request.ProviderOptions);
        payload["model"] = NativeModel(request.Model);
        payload["input"] = request.Text;
        if (request.Voice is not null) payload["voice"] = request.Voice;
        if (!payload.TryGetValue("voice", out var voice) || string.IsNullOrWhiteSpace(voice?.ToString()))
            throw new ArgumentException("InferexAI requires a voice from /v1/models.");
        if (request.OutputFormat is not null) payload["response_format"] = request.OutputFormat;
        if (request.Speed is not null) payload["speed"] = request.Speed;
        if (rate is not null)
            payload["sample_rate"] = ProviderNumericConversions.ToInt32Exact(rate, "outputFormat.rate");
        if (!payload.ContainsKey("response_format")) payload["response_format"] = streaming ? "pcm" : "mp3";
        if (streaming)
        {
            if (payload["response_format"]?.ToString() is not ("pcm" or "mulaw"))
                throw new NotSupportedException("InferexAI streaming speech produces pcm or mulaw only.");
            if (!payload.ContainsKey("stream_format")) payload["stream_format"] = "sse";
            if (payload["stream_format"] is not null && payload["stream_format"]?.ToString() != "sse")
                throw new NotSupportedException("stream_format must be sse or null (raw streaming).");
            if (payload["stream_format"] is null) payload.Remove("stream_format");
        }
        else if (payload.TryGetValue("stream_format", out var streamFormat) && streamFormat is not null)
            throw new ArgumentException("Use the streaming speech endpoint for stream_format.");
        return payload;
    }

    private static List<object> SpeechWarnings(SpeechRequest request)
    {
        var warnings = new List<object>();
        if (request.Instructions is not null) warnings.Add(new { type = "unsupported", feature = "instructions" });
        if (request.Language is not null) warnings.Add(new { type = "unsupported", feature = "language", details = "InferexAI documents no top-level TTS language parameter." });
        return warnings;
    }

    private HttpRequestMessage SpeechMessage(Dictionary<string, object?> payload)
    {
        ApplyAuthHeader();
        return new(HttpMethod.Post, "v1/audio/speech")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    private static string SpeechMime(string format) => format switch
    {
        "mp3" => "audio/mpeg", "wav" => "audio/wav", "opus" => "audio/ogg", "flac" => "audio/flac",
        "aac" => "audio/aac", "pcm" => "audio/L16", "mulaw" => "audio/basic",
        _ => throw new NotSupportedException($"Undocumented InferexAI audio format '{format}'.")
    };

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        var payload = SpeechPayload(request, false);
        var format = payload["response_format"]!.ToString()!;
        var mime = SpeechMime(format);
        using var message = SpeechMessage(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && contentType != mime && contentType != "application/octet-stream")
            throw new InvalidOperationException($"InferexAI returned unexpected audio content type '{contentType}'.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0) throw new InvalidOperationException("InferexAI returned empty audio.");
        return new()
        {
            Audio = new() { Base64 = Convert.ToBase64String(bytes), Format = format, MimeType = response.Content.Headers.ContentType?.MediaType ?? mime },
            Warnings = SpeechWarnings(request),
            Request = new() { Body = payload },
            Response = new() { Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{NativeModel(request.Model)}", Headers = response.GetHeaders() },
            ProviderMetadata = Metadata(JsonSerializer.SerializeToElement(new { headers = response.GetHeaders(), format, bytes = bytes.Length }))
        };
    }

    private sealed record SpeechChunk(JsonElement Raw, string? Audio, bool Done, string Format,
        double? Rate, Dictionary<string, string>? Headers = null);

    private async IAsyncEnumerable<SpeechChunk> ReadSpeech(
        Dictionary<string, object?> payload, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var format = payload["response_format"]!.ToString()!;
        double? rate = format == "mulaw" ? 8000 : payload.TryGetValue("sample_rate", out var sampleRate)
            ? JsonSerializer.SerializeToElement(sampleRate).GetDouble() : null;
        using var message = SpeechMessage(payload);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var headers = response.GetHeaders();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        long bytes = 0;
        if (payload.GetValueOrDefault("stream_format")?.ToString() == "sse")
        {
            if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
                throw new InvalidOperationException("InferexAI returned a non-SSE speech response.");
            yield return new(JsonSerializer.SerializeToElement(new { headers }), null, false, format, rate, headers);
            await foreach (var frame in SseParser.Create(stream).EnumerateAsync(cancellationToken))
            {
                using var document = JsonDocument.Parse(frame.Data);
                var root = document.RootElement;
                var kind = String(root, "type") ?? frame.EventType;
                if (kind == "error" || root.TryGetProperty("error", out _))
                    throw new InvalidOperationException($"InferexAI speech stream failed: {root}");
                if (kind == "speech.audio.delta")
                {
                    var audio = root.GetProperty("audio").GetString()!;
                    bytes += Convert.FromBase64String(audio).Length;
                    yield return new(root.Clone(), audio, false, format, rate);
                }
                else if (kind == "speech.audio.done")
                {
                    if (bytes == 0 || format == "pcm" && bytes % 2 != 0)
                        throw new InvalidOperationException("InferexAI returned empty or truncated SSE audio.");
                    yield return new(root.Clone(), null, true, String(root, "format") ?? String(root, "response_format") ?? format,
                        Number(root, "sample_rate") ?? rate);
                    yield break;
                }
                else
                    yield return new(root.Clone(), null, false, format, rate);
            }
            throw new InvalidOperationException("InferexAI speech stream ended without speech.audio.done.");
        }
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && contentType != SpeechMime(format) && contentType != "application/octet-stream")
            throw new InvalidOperationException($"InferexAI returned unexpected audio content type '{contentType}'.");
        yield return new(JsonSerializer.SerializeToElement(new { headers }), null, false, format, rate, headers);
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            bytes += read;
            var audio = Convert.ToBase64String(buffer, 0, read);
            yield return new(JsonSerializer.SerializeToElement(new { audio }), audio, false, format, rate);
        }
        if (bytes == 0 || format == "pcm" && bytes % 2 != 0)
            throw new InvalidOperationException("InferexAI returned empty or truncated raw audio.");
        yield return new(JsonSerializer.SerializeToElement(new { format, sample_rate = rate, bytes, headers }), null, true, format, rate);
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is not { Length: > 0 } || request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("InferexAI requires non-null ordered text parts.");
        var text = string.Concat(request.Text.Select(p => p.Text));
        Dictionary<string, object?>? payload = null;
        var warnings = new List<object>();
        foreach (var part in request.Text)
        {
            var options = Options(request.ProviderOptions);
            foreach (var option in Options(part.ProviderOptions)) options[option.Key] = option.Value;
            var speech = new SpeechRequest
            {
                Model = request.Model, Text = text, Voice = part.Voice ?? request.Voice,
                OutputFormat = request.OutputFormat?.Type, Speed = request.Speed,
                Instructions = request.Instructions, Language = request.Language,
                ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(options) }
            };
            var current = SpeechPayload(speech, true, request.OutputFormat?.Rate);
            if (payload is not null && !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(payload), JsonSerializer.SerializeToElement(current)))
                throw new NotSupportedException("InferexAI requires one effective voice and configuration per speech request.");
            payload = current;
            warnings.AddRange(SpeechWarnings(speech));
        }
        yield return new SpeechStreamStartPart { Warnings = warnings.DistinctBy(w => JsonSerializer.Serialize(w)) };
        await foreach (var chunk in ReadSpeech(payload!, cancellationToken))
        {
            if (request.IncludeRawChunks == true) yield return new SpeechRawPart { RawValue = chunk.Raw };
            if (chunk.Headers is not null || chunk.Audio is null && !chunk.Done)
                yield return new SpeechResponseMetadataPart { ModelId = request.Model, Timestamp = DateTime.UtcNow, Headers = chunk.Headers, Body = chunk.Raw };
            if (chunk.Audio is not null)
                yield return new SpeechAudioPart
                {
                    Audio = chunk.Audio, AudioFormat = new() { Type = SpeechMime(chunk.Format), Rate = chunk.Rate },
                    ProviderMetadata = Metadata(chunk.Raw)
                };
            if (chunk.Done)
                yield return new SpeechFinishPart
                {
                    ProviderMetadata = Metadata(chunk.Raw),
                    Usage = chunk.Raw.TryGetProperty("usage", out var usage) ? new()
                    {
                        InputTokens = usage.TryGetProperty("input_tokens", out var input) ? input.GetInt64() : null,
                        OutputTokens = usage.TryGetProperty("output_tokens", out var output) ? output.GetInt64() : null,
                        Raw = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    } : null
                };
        }
    }

    private static SpeechRequest ToSpeech(AudioSpeechRequest options)
    {
        var request = options.ToSpeechRequest();
        if (options.AdditionalProperties?.TryGetValue("providerOptions", out var providerOptions) == true)
            request.ProviderOptions = providerOptions.Deserialize<Dictionary<string, JsonElement>>();
        else if (options.AdditionalProperties?.TryGetValue(ProviderId, out var native) == true)
            request.ProviderOptions = Metadata(native);
        else if (options.AdditionalProperties is { Count: > 0 })
            request.ProviderOptions = Metadata(JsonSerializer.SerializeToElement(options.AdditionalProperties));
        return request;
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        if (options.StreamFormat is not null)
            throw new ArgumentException("Use the streaming speech endpoint for stream_format.");
        return (await SpeechRequest(ToSpeech(options), cancellationToken)).ToOpenAISpeechAudio();
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (options.StreamFormat is not null and not "sse")
            throw new NotSupportedException("InferexAI stream_format must be sse.");
        var payload = SpeechPayload(ToSpeech(options), true);
        if (options.StreamFormat is not null) payload["stream_format"] = options.StreamFormat;
        await foreach (var chunk in ReadSpeech(payload, cancellationToken))
        {
            if (chunk.Audio is not null) yield return new NativeSpeechDelta { Audio = chunk.Audio, AdditionalProperties = ExtraSpeechFields(chunk.Raw, "audio", "type") };
            if (chunk.Done)
            {
                var extra = ExtraSpeechFields(chunk.Raw, "type", "usage");
                extra["providerMetadata"] = JsonSerializer.SerializeToElement(Metadata(chunk.Raw));
                yield return new NativeSpeechDone
                {
                    Usage = chunk.Raw.TryGetProperty("usage", out var usage) ? usage.Deserialize<AudioSpeechUsage>() : null,
                    AdditionalProperties = extra
                };
            }
        }
    }

    private static Dictionary<string, JsonElement> ExtraSpeechFields(JsonElement root, params string[] excluded)
        => root.EnumerateObject().Where(p => !excluded.Contains(p.Name)).ToDictionary(p => p.Name, p => p.Value.Clone());

    public sealed class NativeSpeechDelta : AudioSpeechStreamDelta
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }

    public sealed class NativeSpeechDone : AudioSpeechStreamDone
    {
        [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
    }
}
