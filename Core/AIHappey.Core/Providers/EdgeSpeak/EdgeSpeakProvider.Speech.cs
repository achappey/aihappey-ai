using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EdgeSpeak;

public sealed partial class EdgeSpeakProvider
{
    private sealed record SpeechPlan(Dictionary<string, object?> Body, bool Package, bool Buffered);

    public async Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var plan = PrepareSpeech(request, streaming: false);
        using var message = CreateMessage(HttpMethod.Post, "v1/audio/speech", plan.Body, bufferedSpeech: plan.Buffered);
        message.Headers.Accept.Clear();
        message.Headers.Accept.Add(new(plan.Package ? "application/zip" : "audio/wav"));
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccess(response, cancellationToken);
        var mime = plan.Package ? "application/zip" : "audio/wav";
        if (!string.Equals(response.Content.Headers.ContentType?.MediaType, mime, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"EdgeSpeak returned unexpected speech content type '{response.Content.Headers.ContentType}'.");
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
            throw new InvalidOperationException("EdgeSpeak returned empty speech audio.");
        return new()
        {
            Audio = new() { Base64 = Convert.ToBase64String(bytes), MimeType = mime, Format = plan.Package ? "zip" : "wav" },
            Request = new() { Body = plan.Body },
            Response = new()
            {
                Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{plan.Body["model"]}",
                Headers = response.GetHeaders()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => (await SpeechRequest(ToSpeech(options), cancellationToken)).ToOpenAISpeechAudio();

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.StreamFormat is not null and not "sse")
            throw new NotSupportedException("EdgeSpeak event streaming requires stream_format=sse.");
        var plan = PrepareSpeech(ToSpeech(options), streaming: true);
        await foreach (var item in StreamSpeech(plan, cancellationToken))
        {
            if (item.Type == "speech.audio.delta")
                yield return new AudioSpeechStreamDelta { Audio = SpeechAudio(item.Data) };
            else if (item.Type == "speech.audio.done")
                yield return new AudioSpeechStreamDone
                {
                    Usage = item.Data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                        ? usage.Deserialize<AudioSpeechUsage>() : null
                };
        }
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null || request.Text.Length == 0 ||
            request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("EdgeSpeak streaming speech requires text parts.");
        var native = NativeOptions(request.ProviderOptions);
        var speech = new SpeechRequest
        {
            Model = request.Model, Text = string.Concat(request.Text.Select(p => p.Text)),
            Voice = request.Voice, OutputFormat = request.OutputFormat?.Type, Language = request.Language,
            Instructions = request.Instructions, Speed = request.Speed, ProviderOptions = request.ProviderOptions
        };
        var plan = PrepareSpeech(speech, streaming: true,
            ProviderNumericConversions.ToInt32Exact(request.OutputFormat?.Rate, "outputFormat.rate"));
        if (request.Speed is not null)
            plan.Body["speed"] = request.Speed;
        if (request.Text.Any(p => p.Voice is not null || p.ProviderOptions is not null))
        {
            if (native.ContainsKey("segments"))
                throw new ArgumentException("Use either text-part overrides or native segments, not both.");
            var segments = new List<Dictionary<string, object?>>();
            foreach (var part in request.Text)
            {
                if (part.Text.Length == 0)
                    continue;
                var overrides = NativeOptions(part.ProviderOptions);
                if (overrides.Keys.Any(k => k is not ("voice" or "instructions" or "disable_style" or "speed" or "language")))
                    throw new NotSupportedException("EdgeSpeak text parts accept only native segment overrides.");
                var segment = overrides.ToDictionary(p => p.Key, p => (object?)p.Value);
                if (part.Voice is not null)
                    segment["voice"] = part.Voice;
                var previous = segments.LastOrDefault();
                if (previous is not null && JsonElement.DeepEquals(
                    JsonSerializer.SerializeToElement(previous.Where(p => p.Key != "input").ToDictionary()),
                    JsonSerializer.SerializeToElement(segment)))
                    previous["input"] = (string)previous["input"]! + part.Text;
                else
                {
                    segment["input"] = part.Text;
                    segments.Add(segment);
                }
            }
            plan.Body.Remove("input");
            plan.Body["segments"] = segments;
            ValidateSegments(JsonSerializer.SerializeToElement(segments));
        }
        yield return new SpeechStreamStartPart();
        await foreach (var item in StreamSpeech(plan, cancellationToken))
        {
            if (item.Type == "response-metadata")
            {
                yield return new SpeechResponseMetadataPart
                {
                    Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{plan.Body["model"]}", Headers = item.Headers
                };
                continue;
            }
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = new { @event = item.Type, data = item.Data } };
            cancellationToken.ThrowIfCancellationRequested();
            if (item.Type == "speech.audio.delta")
                yield return new SpeechAudioPart
                {
                    Audio = SpeechAudio(item.Data),
                    AudioFormat = new() { Type = "audio/wav", Rate = request.OutputFormat?.Rate }
                };
            else if (item.Type == "speech.audio.done")
                yield return new SpeechFinishPart { ProviderMetadata = Metadata(item.Data) };
        }
    }

    private async IAsyncEnumerable<NativeEvent> StreamSpeech(
        SpeechPlan plan, [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var message = CreateMessage(HttpMethod.Post, "v1/audio/speech", plan.Body, streaming: true);
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        await EnsureSuccess(response, ct);
        yield return new("response-metadata", default, response.GetHeaders());
        var audioReceived = false;
        await foreach (var item in ReadEvents(response, "speech.audio.done", ct))
        {
            if (item.Type == "speech.audio.delta")
            {
                _ = SpeechAudio(item.Data);
                audioReceived = true;
            }
            else if (item.Type == "speech.audio.done" && !audioReceived)
                throw new InvalidOperationException("EdgeSpeak completed speech without audio.");
            yield return item;
        }
    }

    private static string SpeechAudio(JsonElement payload)
    {
        var audio = String(payload, "audio")
            ?? throw new InvalidOperationException("EdgeSpeak speech delta is missing audio.");
        if (Convert.FromBase64String(audio).Length == 0)
            throw new InvalidOperationException("EdgeSpeak speech delta contains empty audio.");
        return audio;
    }

    private static SpeechRequest ToSpeech(AudioSpeechRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var request = options.ToSpeechRequest();
        request.ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(ExtensionOptions(options.AdditionalProperties)) };
        return request;
    }

    private static (string Model, string? Voice) ParseSpeechModel(string model)
    {
        var native = NativeModel(model);
        var slash = native.LastIndexOf('/');
        if (slash >= 0 && (native[(slash + 1)..].StartsWith("builtin:", StringComparison.Ordinal) ||
            native[(slash + 1)..].StartsWith("user:", StringComparison.Ordinal)))
            return (native[..slash], native[(slash + 1)..]);
        return (native, null);
    }

    private static SpeechPlan PrepareSpeech(SpeechRequest request, bool streaming, int? rate = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (model, modelVoice) = ParseSpeechModel(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        var body = NativeOptions(request.ProviderOptions).ToDictionary(p => p.Key, p => (object?)p.Value);
        void Set(string key, object? value)
        {
            if (value is not null)
                body[key] = value;
        }
        body["model"] = model;
        Set("voice", request.Voice);
        if (modelVoice is not null)
        {
            if (request.Voice is not null && request.Voice != modelVoice)
                throw new ArgumentException("EdgeSpeak voice conflicts with the model's voice shortcut.");
            body["voice"] = modelVoice;
        }
        Set("language", request.Language);
        Set("instructions", request.Instructions);
        Set("speed", request.Speed);
        Set("sample_rate", rate);
        var format = request.OutputFormat ??
            (body.TryGetValue("response_format", out var nativeFormat) ? JsonSerializer.SerializeToElement(nativeFormat).GetString() : null);
        if (format is not null && format.ToLowerInvariant() is not ("wav" or "audio/wav" or "audio/x-wav"))
            throw new NotSupportedException("EdgeSpeak speech supports WAV audio only.");
        body["response_format"] = "wav";
        body["stream_format"] = streaming ? "sse" : "audio";
        if (body.TryGetValue("segments", out var segments))
        {
            ValidateSegments(JsonSerializer.SerializeToElement(segments));
            body.Remove("input");
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
            body["input"] = request.Text;
        }
        var json = JsonSerializer.SerializeToElement(body);
        if (string.IsNullOrWhiteSpace(String(json, "voice")))
            throw new ArgumentException("EdgeSpeak requires a voice ID from /v1/audio/voices.");
        if (json.TryGetProperty("disable_style", out var disabled) && disabled.ValueKind == JsonValueKind.True &&
            !string.IsNullOrEmpty(String(json, "instructions")))
            throw new ArgumentException("EdgeSpeak disable_style conflicts with non-empty instructions.");
        var container = String(json, "output_container") ?? "audio";
        if (container is not ("audio" or "zip"))
            throw new ArgumentException("EdgeSpeak output_container must be audio or zip.");
        var music = json.TryGetProperty("music", out var musicOptions) && musicOptions.ValueKind != JsonValueKind.Null;
        if ((music || container == "zip") && model != "m-a-p/YuE2-3B")
            throw new NotSupportedException("EdgeSpeak music and ZIP output require m-a-p/YuE2-3B.");
        if (streaming && (music || container == "zip"))
            throw new NotSupportedException("EdgeSpeak music and ZIP output require buffered speech.");
        return new(body, container == "zip", music || container == "zip");
    }

    private static void ValidateSegments(JsonElement segments)
    {
        if (segments.ValueKind != JsonValueKind.Array || segments.GetArrayLength() == 0)
            throw new ArgumentException("EdgeSpeak segments must be a non-empty array.");
        foreach (var segment in segments.EnumerateArray())
        {
            if (segment.ValueKind != JsonValueKind.Object || String(segment, "input") is null)
                throw new ArgumentException("Every EdgeSpeak speech segment requires input.");
            if (segment.TryGetProperty("disable_style", out var disabled) && disabled.ValueKind == JsonValueKind.True &&
                !string.IsNullOrEmpty(String(segment, "instructions")))
                throw new ArgumentException("EdgeSpeak segment disable_style conflicts with instructions.");
        }
    }
}
