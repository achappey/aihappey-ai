using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ZenMux;

public partial class ZenMuxProvider
{
    private static readonly JsonSerializerOptions ZenMuxAudioJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        ValidateZenMuxSpeechOptions(request.ProviderOptions);
        if (!string.IsNullOrWhiteSpace(request.Instructions) || !string.IsNullOrWhiteSpace(request.Language))
            throw new NotSupportedException("ZenMux speech does not document instructions or language parameters.");

        var format = request.OutputFormat ?? "pcm";
        ValidateZenMuxSpeechFormat(format);
        var payload = CreateZenMuxSpeechPayload(
            request.Model, request.Text, request.Voice, request.Speed, format, stream: false);
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = JsonContent(payload)
        };
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ZenMux speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var audio = root.GetProperty("audio").GetString()
            ?? throw new InvalidOperationException("ZenMux speech response did not contain audio.");
        var mimeType = root.GetProperty("mime_type").GetString()
            ?? throw new InvalidOperationException("ZenMux speech response did not contain a MIME type.");
        return new SpeechResponse
        {
            Audio = new() { Base64 = audio, MimeType = mimeType, Format = format },
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Warnings = [],
            Request = new() { Body = payload },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public async Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        if (!string.IsNullOrWhiteSpace(options.Instructions))
            throw new NotSupportedException("ZenMux speech does not document instructions.");
        if (!string.IsNullOrWhiteSpace(options.StreamFormat))
            throw new NotSupportedException("ZenMux speech uses the stream boolean and does not document stream_format.");
        if (options.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("ZenMux speech does not document additional request fields.");

        var format = options.ResponseFormat ?? "pcm";
        ValidateZenMuxSpeechFormat(format);
        var payload = CreateZenMuxSpeechPayload(
            options.Model, options.Input, options.Voice, options.Speed, format, stream: false);
        ApplyAuthHeader();
        using var response = await _client.PostAsync(
            "v1/audio/speech",
            JsonContent(payload),
            cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ZenMux speech request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var base64 = root.GetProperty("audio").GetString()
            ?? throw new InvalidOperationException("ZenMux speech response did not contain audio.");
        var mimeType = root.GetProperty("mime_type").GetString()
            ?? throw new InvalidOperationException("ZenMux speech response did not contain a MIME type.");
        try
        {
            return (Convert.FromBase64String(base64), mimeType);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException("ZenMux speech response contained invalid base64 audio.", exception);
        }
    }

    public async IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Input);
        if (!string.IsNullOrWhiteSpace(options.Instructions) || !string.IsNullOrWhiteSpace(options.StreamFormat)
            || options.AdditionalProperties?.Count > 0)
            throw new NotSupportedException("ZenMux speech does not document instructions, stream_format, or additional request fields.");

        var format = options.ResponseFormat ?? "pcm";
        ValidateZenMuxSpeechFormat(format);
        var payload = CreateZenMuxSpeechPayload(
            options.Model, options.Input, options.Voice, options.Speed, format, stream: true);
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = JsonContent(payload)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        await foreach (var data in ReadZenMuxSseAsync(message, "speech", cancellationToken))
        {
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                continue;
            switch (type.GetString())
            {
                case "speech.audio.delta":
                    yield return JsonSerializer.Deserialize<AudioSpeechStreamDelta>(
                        data.GetRawText(), ZenMuxAudioJsonOptions)
                        ?? throw new InvalidOperationException("ZenMux returned an invalid speech delta.");
                    break;
                case "speech.audio.done":
                    yield return JsonSerializer.Deserialize<AudioSpeechStreamDone>(
                        data.GetRawText(), ZenMuxAudioJsonOptions)
                        ?? throw new InvalidOperationException("ZenMux returned an invalid speech completion.");
                    break;
                case "error":
                    throw new InvalidOperationException($"ZenMux speech stream returned an error: {data.GetRawText()}");
            }
        }
    }

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        if (request.Text is null || request.Text.Length == 0)
            throw new ArgumentException("At least one speech text part is required.", nameof(request));
        if (request.Text.Any(part => part is null || !string.Equals(part.Type, "text", StringComparison.Ordinal)))
            throw new NotSupportedException("ZenMux speech supports text input parts only.");
        if (request.Text.Any(part => part.ProviderOptions?.Count > 0))
            throw new NotSupportedException("ZenMux speech does not document text-part provider options.");
        if (!string.IsNullOrWhiteSpace(request.Instructions) || !string.IsNullOrWhiteSpace(request.Language))
            throw new NotSupportedException("ZenMux speech does not document instructions or language parameters.");
        ValidateZenMuxSpeechOptions(request.ProviderOptions);

        var voices = request.Text.Select(part => part.Voice)
            .Where(voice => !string.IsNullOrWhiteSpace(voice))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (voices.Length > 1)
            throw new NotSupportedException("ZenMux speech accepts one voice per request.");
        var voice = request.Voice ?? voices.FirstOrDefault();
        var input = string.Concat(request.Text.Select(part => part.Text));
        var format = request.OutputFormat?.Type ?? "pcm";
        ValidateZenMuxSpeechFormat(format);
        if (request.Speed is { } speed && (!double.IsFinite(speed) || speed <= 0 || speed > float.MaxValue))
            throw new ArgumentOutOfRangeException(nameof(request), "Speech speed must be a positive number.");

        var payload = CreateZenMuxSpeechPayload(
            request.Model, input, voice, (float?)request.Speed, format, stream: true);
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/speech")
        {
            Content = JsonContent(payload)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        yield return new SpeechStreamStartPart();
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier())
        };
        var fullUsage = default(StreamingSpeechUsage);
        await foreach (var data in ReadZenMuxSseAsync(message, "speech", cancellationToken))
        {
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                continue;
            switch (type.GetString())
            {
                case "speech.audio.delta":
                    var audio = data.GetProperty("audio").GetString() ?? string.Empty;
                    yield return new SpeechAudioPart
                    {
                        Audio = audio,
                        AudioFormat = new AudioFormat { Type = "pcm", Rate = 24000 },
                        ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata()
                    };
                    break;
                case "speech.audio.done":
                    if (data.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
                    {
                        fullUsage = new StreamingSpeechUsage
                        {
                            InputTokens = ReadZenMuxLong(usage, "input_tokens"),
                            OutputTokens = ReadZenMuxLong(usage, "output_tokens"),
                            Raw = new() { ["usage"] = usage.Clone() }
                        };
                    }
                    break;
                case "error":
                    throw new InvalidOperationException($"ZenMux speech stream returned an error: {data.GetRawText()}");
            }
        }
        yield return new SpeechFinishPart { Usage = fullUsage };
    }

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);
        var audio = ReadBase64Audio(request.Audio);
        var format = GetZenMuxAudioFormat(request.MediaType);
        var options = ReadZenMuxAudioOptions(request.ProviderOptions);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input_audio"] = new { data = audio, format }
        };
        AddZenMuxTranscriptionOptions(payload, options);
        ApplyAuthHeader();
        using var response = await _client.PostAsync("v1/audio/transcriptions", JsonContent(payload), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ZenMux transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);

        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var text = root.GetProperty("text").GetString()
            ?? throw new InvalidOperationException("ZenMux transcription response did not contain text.");
        var usage = root.TryGetProperty("usage", out var usageElement) ? usageElement : default;
        return new TranscriptionResponse
        {
            Text = text,
            Language = options.Language,
            DurationInSeconds = ReadZenMuxSingle(usage, "seconds"),
            Segments = [],
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root.Clone()),
            Request = new() { Body = JsonSerializer.Serialize(payload, ZenMuxAudioJsonOptions) },
            Response = new()
            {
                Timestamp = DateTime.UtcNow,
                Headers = response.GetHeaders(),
                ModelId = ReadZenMuxAudioString(root, "model")?.ToModelId(GetIdentifier())
                    ?? request.Model.ToModelId(GetIdentifier()),
                Body = root.Clone()
            }
        };
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentNullException.ThrowIfNull(options.File);
        if (options.Prompt is not null || options.ResponseFormat is not null || options.Temperature.HasValue
            || options.TimestampGranularities is not null || options.Include is not null
            || options.ChunkingStrategy is not null || options.KnownSpeakerNames is not null
            || options.KnownSpeakerReferences is not null
            || options.AdditionalProperties?.Any(property => property.Key != "enable_itn"
                || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) == true)
            throw new NotSupportedException("ZenMux transcriptions document only model, input_audio, language, enable_itn, and stream.");

        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input_audio"] = new
            {
                data = Convert.ToBase64String(memory.ToArray()),
                format = GetZenMuxAudioFormat(options.File.ContentType, options.File.FileName)
            }
        };
        if (!string.IsNullOrWhiteSpace(options.Language))
            payload["language"] = options.Language;
        if (options.AdditionalProperties?.TryGetValue("enable_itn", out var enableItn) == true)
            payload["enable_itn"] = enableItn.GetBoolean();
        return await SendZenMuxOpenAITranscriptionAsync(payload, cancellationToken);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);
        ArgumentNullException.ThrowIfNull(options.File);
        if (options.Prompt is not null || options.ResponseFormat is not null || options.Temperature.HasValue
            || options.TimestampGranularities is not null || options.Include is not null
            || options.ChunkingStrategy is not null || options.KnownSpeakerNames is not null
            || options.KnownSpeakerReferences is not null
            || options.AdditionalProperties?.Any(property => property.Key != "enable_itn"
                || property.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) == true)
            throw new NotSupportedException("ZenMux transcriptions document only model, input_audio, language, enable_itn, and stream.");

        await using var stream = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await stream.CopyToAsync(memory, cancellationToken);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["input_audio"] = new
            {
                data = Convert.ToBase64String(memory.ToArray()),
                format = GetZenMuxAudioFormat(options.File.ContentType, options.File.FileName)
            },
            ["stream"] = true
        };
        if (!string.IsNullOrWhiteSpace(options.Language))
            payload["language"] = options.Language;
        if (options.AdditionalProperties?.TryGetValue("enable_itn", out var enableItn) == true)
            payload["enable_itn"] = enableItn.GetBoolean();

        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = JsonContent(payload)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        await foreach (var data in ReadZenMuxSseAsync(message, "transcription", cancellationToken))
        {
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                continue;
            switch (type.GetString())
            {
                case "transcript.text.delta":
                    yield return JsonSerializer.Deserialize<OpenAITranscriptionTextDelta>(
                        data.GetRawText(), ZenMuxAudioJsonOptions)
                        ?? throw new InvalidOperationException("ZenMux returned an invalid transcript delta.");
                    break;
                case "transcript.text.done":
                    yield return JsonSerializer.Deserialize<OpenAITranscriptionTextDone>(
                        data.GetRawText(), ZenMuxAudioJsonOptions)
                        ?? throw new InvalidOperationException("ZenMux returned an invalid transcript completion.");
                    break;
                case "error":
                    throw new InvalidOperationException($"ZenMux transcription stream returned an error: {data.GetRawText()}");
            }
        }
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Audio);
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        var audio = ReadBase64Audio(request.Audio);
        var format = GetZenMuxAudioFormat(request.InputAudioFormat.Type);
        var options = ReadZenMuxAudioOptions(request.ProviderOptions);
        var payload = new Dictionary<string, object?>
        {
            ["model"] = request.Model,
            ["input_audio"] = new { data = audio, format },
            ["stream"] = true
        };
        AddZenMuxTranscriptionOptions(payload, options);
        ApplyAuthHeader();
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/audio/transcriptions")
        {
            Content = JsonContent(payload)
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        yield return new TranscriptionStreamStartPart();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier())
        };

        var text = new StringBuilder();
        JsonElement? done = null;
        await foreach (var data in ReadZenMuxSseAsync(message, "transcription", cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new TranscriptionRawPart { RawValue = data };
            if (!data.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
                continue;
            switch (type.GetString())
            {
                case "transcript.text.delta":
                    if (data.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String)
                    {
                        var value = delta.GetString() ?? string.Empty;
                        text.Append(value);
                        if (value.Length > 0)
                            yield return new TranscriptionDeltaPart
                            {
                                Delta = value,
                                ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata()
                            };
                    }
                    break;
                case "transcript.text.done":
                    done = data;
                    if (data.TryGetProperty("text", out var fullText) && fullText.ValueKind == JsonValueKind.String)
                    {
                        text.Clear();
                        text.Append(fullText.GetString());
                    }
                    break;
                case "error":
                    throw new InvalidOperationException($"ZenMux transcription stream returned an error: {data.GetRawText()}");
            }
        }

        yield return new TranscriptionFinishPart
        {
            Text = text.ToString(),
            Segments = [],
            DurationInSeconds = done.HasValue && done.Value.TryGetProperty("usage", out var usage)
                ? ReadZenMuxDouble(usage, "seconds")
                : null,
            ProviderMetadata = done.HasValue
                ? GetIdentifier().CreatePrimitiveProviderMetadata(done.Value)
                : null
        };
    }

    private async Task<IOpenAITranscriptionResponse> SendZenMuxOpenAITranscriptionAsync(
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var response = await _client.PostAsync(
            "v1/audio/transcriptions", JsonContent(payload), cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"ZenMux transcription request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                null,
                response.StatusCode);
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        var result = new OpenAITranscriptionResponse
        {
            Text = root.GetProperty("text").GetString()
                ?? throw new InvalidOperationException("ZenMux transcription response did not contain text.")
        };
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
            && TryGetZenMuxLong(usage, "input_tokens", out var inputTokens)
            && TryGetZenMuxLong(usage, "output_tokens", out var outputTokens)
            && TryGetZenMuxLong(usage, "total_tokens", out var totalTokens))
        {
            result.Usage = new OpenAITranscriptionTokenUsage
            {
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                TotalTokens = totalTokens
            };
        }
        return result;
    }

    private async IAsyncEnumerable<JsonElement> ReadZenMuxSseAsync(
        HttpRequestMessage message,
        string operation,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"ZenMux {operation} stream failed ({(int)response.StatusCode} {response.ReasonPhrase}): {error}",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(responseStream);
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var item in ParseZenMuxSseData(lines))
                    yield return item;
                lines.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                lines.Add(line["data:".Length..].TrimStart());
            }
        }

        foreach (var item in ParseZenMuxSseData(lines))
            yield return item;
    }

    private static IEnumerable<JsonElement> ParseZenMuxSseData(List<string> lines)
    {
        if (lines.Count == 0)
            yield break;
        var data = string.Join("\n", lines).Trim();
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            yield break;
        using var document = JsonDocument.Parse(data);
        yield return document.RootElement.Clone();
    }

    private static Dictionary<string, object?> CreateZenMuxSpeechPayload(
        string model,
        string input,
        string? voice,
        float? speed,
        string format,
        bool stream)
    {
        if (speed.HasValue && (!float.IsFinite(speed.Value) || speed.Value <= 0))
            throw new ArgumentOutOfRangeException(nameof(speed), "Speech speed must be a positive number.");
        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["input"] = input,
            ["response_format"] = format,
            ["stream"] = stream
        };
        if (!string.IsNullOrWhiteSpace(voice))
            payload["voice"] = voice;
        if (speed.HasValue)
            payload["speed"] = speed.Value;
        return payload;
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, ZenMuxAudioJsonOptions), Encoding.UTF8, MediaTypeNames.Application.Json);

    private static void ValidateZenMuxSpeechFormat(string format)
    {
        if (!string.Equals(format, "pcm", StringComparison.OrdinalIgnoreCase))
            throw new NotSupportedException("ZenMux speech currently documents only the pcm response format.");
    }

    private static void ValidateZenMuxSpeechOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("zenmux", out var options) != true)
            return;
        if (options.ValueKind != JsonValueKind.Object || options.EnumerateObject().Any())
            throw new NotSupportedException("ZenMux speech does not document provider-specific options.");
    }

    private static ZenMuxTranscriptionOptions ReadZenMuxAudioOptions(Dictionary<string, JsonElement>? providerOptions)
    {
        if (providerOptions?.TryGetValue("zenmux", out var options) != true)
            return new();
        if (options.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("providerOptions.zenmux must be an object.", nameof(providerOptions));
        var result = new ZenMuxTranscriptionOptions();
        foreach (var property in options.EnumerateObject())
        {
            switch (property.Name)
            {
                case "language" when property.Value.ValueKind == JsonValueKind.String:
                    result.Language = property.Value.GetString();
                    break;
                case "enable_itn" when property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                    result.EnableItn = property.Value.GetBoolean();
                    break;
                default:
                    throw new NotSupportedException($"ZenMux transcriptions do not document provider option '{property.Name}'.");
            }
        }
        return result;
    }

    private static void AddZenMuxTranscriptionOptions(
        Dictionary<string, object?> payload,
        ZenMuxTranscriptionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.Language))
            payload["language"] = options.Language;
        if (options.EnableItn.HasValue)
            payload["enable_itn"] = options.EnableItn.Value;
    }

    private static string ReadBase64Audio(object audio)
    {
        var value = audio switch
        {
            JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
            string text => text,
            _ => audio.ToString()
        };
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Audio is required.", nameof(audio));
        var comma = value.IndexOf(',');
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            value = value[(comma + 1)..];
        try
        {
            _ = Convert.FromBase64String(value);
            return value;
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Audio must be valid base64 data.", nameof(audio), exception);
        }
    }

    private static string GetZenMuxAudioFormat(string mediaType, string? fileName = null)
    {
        var value = mediaType.Split(';', 2)[0].Trim().ToLowerInvariant();
        var format = value switch
        {
            "wav" or "audio/wav" or "audio/wave" or "audio/x-wav" => "wav",
            "mp3" or "audio/mp3" or "audio/mpeg" => "mp3",
            "flac" or "audio/flac" or "audio/x-flac" => "flac",
            "m4a" or "audio/m4a" or "audio/mp4" => "m4a",
            "ogg" or "audio/ogg" => "ogg",
            "webm" or "audio/webm" => "webm",
            "aac" or "audio/aac" => "aac",
            _ => Path.GetExtension(fileName ?? string.Empty).TrimStart('.').ToLowerInvariant()
        };
        if (format is not ("wav" or "mp3" or "flac" or "m4a" or "ogg" or "webm" or "aac"))
            throw new NotSupportedException($"ZenMux transcriptions do not document the audio format '{mediaType}'.");
        return format;
    }

    private static string? ReadZenMuxAudioString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var item)
            && item.ValueKind == JsonValueKind.String ? item.GetString() : null;

    private static float? ReadZenMuxSingle(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.TryGetSingle(out var number) ? number : null;

    private static double? ReadZenMuxDouble(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.TryGetDouble(out var number) ? number : null;

    private static long? ReadZenMuxLong(JsonElement value, string name)
        => TryGetZenMuxLong(value, name, out var number) ? number : null;

    private static bool TryGetZenMuxLong(JsonElement value, string name, out long number)
    {
        number = 0;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item)
            && item.TryGetInt64(out number);
    }

    private sealed class ZenMuxTranscriptionOptions
    {
        public string? Language { get; set; }
        public bool? EnableItn { get; set; }
    }
}
