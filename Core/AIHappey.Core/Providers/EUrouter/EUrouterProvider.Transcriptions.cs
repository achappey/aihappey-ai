using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.EUrouter;

public partial class EUrouterProvider
{
    private const string EUrouterTranscriptionEndpoint = "v1/audio/transcriptions";

    public async Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Model);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaType);

        var payload = GetEUrouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input_audio"] = new JsonObject
        {
            ["data"] = NormalizeEUrouterAudio(request.Audio),
            ["format"] = ResolveEUrouterInputAudioFormat(request.MediaType)
        };
        payload["response_format"] = "verbose_json";
        payload["stream"] = false;

        var result = await SendEUrouterTranscriptionAsync(payload, cancellationToken);
        return CreateEUrouterTranscriptionResponse(result.Root, request.Model, payload, result.Headers);
    }

    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var requestedFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var payload = await CreateEUrouterOpenAITranscriptionPayloadAsync(options, stream: false, cancellationToken);
        var result = await SendEUrouterTranscriptionAsync(payload, cancellationToken);
        var response = CreateEUrouterTranscriptionResponse(result.Root, options.Model, payload, result.Headers);
        return response.ToOpenAITranscriptionResponse(requestedFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.ValidateOpenAITranscriptionRequest();
        var payload = await CreateEUrouterOpenAITranscriptionPayloadAsync(options, stream: true, cancellationToken);
        var events = StreamEUrouterTranscriptionPayloadAsync(payload, cancellationToken);
        await foreach (var item in events.WithCancellation(cancellationToken))
        {
            if (item.Type == "transcript.text.delta" && GetEUrouterString(item.Payload, "delta") is { Length: > 0 } delta)
                yield return new OpenAITranscriptionTextDelta { Delta = delta };
            else if (item.Type == "transcript.text.done")
                yield return new OpenAITranscriptionTextDone { Text = GetEUrouterString(item.Payload, "text") ?? string.Empty };
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
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputAudioFormat.Type);

        var payload = GetEUrouterProviderOptions(request.ProviderOptions);
        payload["model"] = request.Model;
        payload["input_audio"] = new JsonObject
        {
            ["data"] = NormalizeEUrouterAudio(request.Audio),
            ["format"] = ResolveEUrouterInputAudioFormat(request.InputAudioFormat.Type)
        };
        payload["response_format"] = "json";
        payload["stream"] = true;

        yield return new TranscriptionStreamStartPart();
        var completeText = new StringBuilder();
        JsonElement? done = null;
        await foreach (var item in StreamEUrouterTranscriptionPayloadAsync(payload, cancellationToken).WithCancellation(cancellationToken))
        {
            if (request.IncludeRawChunks == true) yield return new TranscriptionRawPart { RawValue = item.Payload };
            if (item.Type == "transcript.text.delta" && GetEUrouterString(item.Payload, "delta") is { Length: > 0 } delta)
            {
                completeText.Append(delta);
                yield return new TranscriptionDeltaPart
                {
                    Delta = delta,
                    ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(item.Payload)
                };
            }
            else if (item.Type == "transcript.text.done")
            {
                done = item.Payload;
                if (GetEUrouterString(item.Payload, "text") is { } text)
                {
                    completeText.Clear();
                    completeText.Append(text);
                }
            }
        }

        yield return new TranscriptionFinishPart
        {
            Text = completeText.ToString(),
            Language = done.HasValue ? GetEUrouterString(done.Value, "language") : null,
            DurationInSeconds = done.HasValue ? GetEUrouterDouble(done.Value, "duration") : null,
            Segments = [],
            ProviderMetadata = done.HasValue ? GetIdentifier().CreatePrimitiveProviderMetadata(done.Value) : null
        };
    }

    private async Task<EUrouterTranscriptionResult> SendEUrouterTranscriptionAsync(JsonObject payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateEUrouterJsonRequest(EUrouterTranscriptionEndpoint, payload);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EUrouter transcription request failed ({(int)response.StatusCode}): {raw}");
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("EUrouter transcription request returned an empty response.");
        using var document = JsonDocument.Parse(raw);
        return new EUrouterTranscriptionResult(document.RootElement.Clone(), response.GetHeaders());
    }

    private async Task<JsonObject> CreateEUrouterOpenAITranscriptionPayloadAsync(
        OpenAITranscriptionRequest options,
        bool stream,
        CancellationToken cancellationToken)
    {
        await using var source = options.File.OpenReadStream();
        using var memory = new MemoryStream();
        await source.CopyToAsync(memory, cancellationToken);

        var payload = CopyEUrouterProperties(options.AdditionalProperties);
        payload["model"] = options.Model;
        payload["input_audio"] = new JsonObject
        {
            ["data"] = Convert.ToBase64String(memory.ToArray()),
            ["format"] = ResolveEUrouterInputAudioFormat(options.File.ContentType, options.File.FileName)
        };
        if (!string.IsNullOrWhiteSpace(options.Language)) payload["language"] = options.Language;
        if (!string.IsNullOrWhiteSpace(options.Prompt)) payload["prompt"] = options.Prompt;
        if (options.Temperature.HasValue) payload["temperature"] = options.Temperature.Value;
        if (options.TimestampGranularities?.Length > 0)
            payload["timestamp_granularities"] = JsonSerializer.SerializeToNode(options.TimestampGranularities);
        if (options.Include?.Length > 0) payload["include"] = JsonSerializer.SerializeToNode(options.Include);
        if (options.ChunkingStrategy is not null) payload["chunking_strategy"] = JsonSerializer.SerializeToNode(options.ChunkingStrategy);
        payload["response_format"] = stream ? "json" : "verbose_json";
        payload["stream"] = stream;
        return payload;
    }

    private async IAsyncEnumerable<EUrouterTranscriptionEvent> StreamEUrouterTranscriptionPayloadAsync(
        JsonObject payload,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = CreateEUrouterJsonRequest(EUrouterTranscriptionEndpoint, payload, acceptSse: true);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"EUrouter streaming transcription request failed ({(int)response.StatusCode}): {error}");
        }

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) != true)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement.Clone();
            if (GetEUrouterString(root, "text") is { Length: > 0 } text)
                yield return new EUrouterTranscriptionEvent("transcript.text.delta", JsonSerializer.SerializeToElement(new { type = "transcript.text.delta", delta = text }));
            yield return new EUrouterTranscriptionEvent("transcript.text.done", root);
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        var dataLines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                foreach (var item in ParseEUrouterTranscriptionEvent(dataLines)) yield return item;
                dataLines.Clear();
            }
            else if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                dataLines.Add(line[5..].TrimStart());
        }
        foreach (var item in ParseEUrouterTranscriptionEvent(dataLines)) yield return item;
    }

    private static IEnumerable<EUrouterTranscriptionEvent> ParseEUrouterTranscriptionEvent(List<string> dataLines)
    {
        if (dataLines.Count == 0) yield break;
        var data = string.Join("\n", dataLines).Trim();
        if (string.IsNullOrWhiteSpace(data) || data == "[DONE]") yield break;
        using var document = JsonDocument.Parse(data);
        var payload = document.RootElement.Clone();
        var type = GetEUrouterString(payload, "type");
        if (type == "error") throw new InvalidOperationException($"EUrouter streaming transcription returned an error: {data}");
        if (type is "transcript.text.delta" or "transcript.text.done")
            yield return new EUrouterTranscriptionEvent(type, payload);
        else if (type is null && GetEUrouterString(payload, "text") is { Length: > 0 } text)
        {
            yield return new EUrouterTranscriptionEvent("transcript.text.delta", JsonSerializer.SerializeToElement(new { type = "transcript.text.delta", delta = text }));
            yield return new EUrouterTranscriptionEvent("transcript.text.done", payload);
        }
    }

    private TranscriptionResponse CreateEUrouterTranscriptionResponse(
        JsonElement root,
        string model,
        JsonObject payload,
        IDictionary<string, string> headers)
    {
        var segments = root.TryGetProperty("segments", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Select(item => new TranscriptionSegment
            {
                Text = GetEUrouterString(item, "text") ?? string.Empty,
                StartSecond = (float)(GetEUrouterDouble(item, "start") ?? 0),
                EndSecond = (float)(GetEUrouterDouble(item, "end") ?? 0)
            }).ToList()
            : [];
        return new TranscriptionResponse
        {
            Text = GetEUrouterString(root, "text") ?? string.Join(" ", segments.Select(static item => item.Text)),
            Language = GetEUrouterString(root, "language"),
            DurationInSeconds = (float?)GetEUrouterDouble(root, "duration"),
            Segments = segments,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(root),
            Request = new TranscriptionRequestItem { Body = payload.ToJsonString(EUrouterAudioJsonOptions) },
            Response = new ResponseData
            {
                Timestamp = DateTime.UtcNow,
                Headers = headers,
                ModelId = model.ToModelId(GetIdentifier()),
                Body = root
            }
        };
    }

    private static string NormalizeEUrouterAudio(object? audio)
    {
        var value = audio is JsonElement element && element.ValueKind == JsonValueKind.String ? element.GetString() : audio?.ToString();
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("Audio is required.", nameof(audio));
        var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && marker < 0)
            throw new ArgumentException("Audio data URL must use base64 encoding.", nameof(audio));
        value = marker >= 0 ? value[(marker + 8)..] : value;
        try { Convert.FromBase64String(value); }
        catch (FormatException exception) { throw new ArgumentException("Audio must contain valid base64 data.", nameof(audio), exception); }
        return value;
    }

    private static string ResolveEUrouterInputAudioFormat(string? mediaType, string? fileName = null)
    {
        var extension = Path.GetExtension(fileName)?.TrimStart('.').ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(extension)) return extension;
        return mediaType?.Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3", "audio/mp4" or "audio/x-m4a" => "m4a",
            "audio/wav" or "audio/x-wav" => "wav", "audio/webm" => "webm", "audio/ogg" => "ogg",
            "audio/flac" => "flac", var value when !string.IsNullOrWhiteSpace(value) && value.Contains('/') => value.Split('/').Last(),
            var value when !string.IsNullOrWhiteSpace(value) => value, _ => "wav"
        };
    }

    private static string? GetEUrouterString(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static double? GetEUrouterDouble(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.TryGetDouble(out var number) ? number : null;

    private sealed record EUrouterTranscriptionResult(JsonElement Root, IDictionary<string, string> Headers);
    private sealed record EUrouterTranscriptionEvent(string Type, JsonElement Payload);
}
