using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.KalpaLabs;

public partial class KalpaLabsProvider
{
    private sealed record SpeechChunk(string Kind, JsonElement Data, string? Audio = null, int Rate = 24000,
        IDictionary<string, string>? Headers = null);

    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null || request.Text.Length == 0 ||
            request.Text.Any(p => p is null || p.Type != "text" || p.Text is null))
            throw new ArgumentException("KalpaLabs requires text parts with non-null text.");
        var text = string.Concat(request.Text.Select(p => p.Text));
        SpeechPlan? plan = null;
        foreach (var part in request.Text)
        {
            var current = PrepareSpeech(new SpeechRequest
            {
                Model = request.Model, Text = text, Voice = part.Voice ?? request.Voice,
                OutputFormat = request.OutputFormat?.Type, Instructions = request.Instructions,
                Speed = (float?)request.Speed, Language = request.Language,
                ProviderOptions = MergeOptions(request.ProviderOptions, part.ProviderOptions)
            }, streaming: true, request.OutputFormat?.Rate);
            if (plan is not null && (plan.Path != current.Path ||
                !JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan.Payload, Json), JsonSerializer.SerializeToElement(current.Payload, Json))))
                throw new NotSupportedException("KalpaLabs HTTP speech requires one effective voice and configuration per script.");
            if (plan is null)
                plan = current;
            else
                plan.Warnings.AddRange(current.Warnings);
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechStreamStartPart { Warnings = plan!.Warnings.DistinctBy(w => JsonSerializer.Serialize(w)) };
        await foreach (var chunk in StreamSpeech(plan, cancellationToken))
        {
            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = new { @event = chunk.Kind, data = chunk.Data } };
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Kind == "meta")
                yield return new SpeechResponseMetadataPart
                {
                    Timestamp = DateTime.UtcNow, ModelId = $"{ProviderId}/{plan.Payload["model"]}",
                    Body = chunk.Data, Headers = chunk.Headers
                };
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Audio is not null)
                yield return new SpeechAudioPart
                {
                    Audio = chunk.Audio, AudioFormat = new() { Type = "audio/pcm", Rate = chunk.Rate }
                };
            if (chunk.Kind == "end")
                yield return new SpeechFinishPart
                {
                    Usage = chunk.Data.TryGetProperty("usage", out var usage) ? new()
                    {
                        InputCharacters = usage.GetProperty("input_chars").GetInt64(),
                        Raw = usage.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone())
                    } : null,
                    ProviderMetadata = Metadata(chunk.Data)
                };
        }
    }

    private async IAsyncEnumerable<SpeechChunk> StreamSpeech(
        SpeechPlan plan, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sse = plan.Path == "v1/converse/stream";
        cancellationToken.ThrowIfCancellationRequested();
        using var message = CreateMessage(HttpMethod.Post, plan.Path, plan.Payload, sse ? "text/event-stream" : "audio/pcm");
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && contentType != (sse ? "text/event-stream" : "audio/pcm"))
            throw new InvalidOperationException($"KalpaLabs returned unexpected stream content type '{contentType}'.");
        var headers = response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (sse)
        {
            var sawMeta = false;
            var rate = plan.Rate;
            await foreach (var (kind, data) in ReadSse(stream, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (kind == "error")
                    throw new InvalidOperationException($"KalpaLabs stream failed: {data.GetRawText()}");
                if (kind == "meta")
                {
                    if (sawMeta)
                        throw new InvalidOperationException("KalpaLabs returned duplicate stream metadata.");
                    rate = data.GetProperty("sample_rate").GetInt32();
                    if (rate <= 0)
                        throw new InvalidOperationException("KalpaLabs returned an invalid sample rate.");
                    sawMeta = true;
                    yield return new(kind, data, Rate: rate, Headers: headers);
                }
                else if (kind == "audio")
                {
                    if (!sawMeta)
                        throw new InvalidOperationException("KalpaLabs returned audio before stream metadata.");
                    var audio = data.GetProperty("pcm_b64").GetString()!;
                    ValidateAudio(audio, pcm: true);
                    yield return new(kind, data, audio, rate);
                }
                else if (kind == "end")
                {
                    if (!sawMeta)
                        throw new InvalidOperationException("KalpaLabs stream ended without metadata.");
                    if (data.GetProperty("sample_rate").GetInt32() != rate)
                        throw new InvalidOperationException("KalpaLabs changed the sample rate within a stream.");
                    _ = data.GetProperty("text").GetString();
                    _ = data.GetProperty("usage").GetProperty("input_chars").GetInt64();
                    yield return new(kind, data, Rate: rate);
                    yield break;
                }
                else
                    throw new InvalidOperationException($"Unknown KalpaLabs SSE event '{kind}'.");
            }
            throw new InvalidOperationException("KalpaLabs SSE stream ended without an end event.");
        }

        yield return new("meta", JsonSerializer.SerializeToElement(new { sample_rate = plan.Rate, headers }), Headers: headers);
        var buffer = new byte[8193];
        var pending = 0;
        long bytes = 0;
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(pending, buffer.Length - pending), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
                break;
            var available = read + pending;
            var whole = available - available % 2;
            var audio = whole > 0 ? Convert.ToBase64String(buffer, 0, whole) : null;
            pending = available - whole;
            if (pending != 0)
                buffer[0] = buffer[whole];
            bytes += whole;
            if (audio is not null)
                yield return new("audio", JsonSerializer.SerializeToElement(new { pcm_b64 = audio }), audio, plan.Rate);
        }
        if (pending != 0 || bytes == 0)
            throw new InvalidOperationException("KalpaLabs returned empty or incomplete PCM audio.");
        cancellationToken.ThrowIfCancellationRequested();
        yield return new("end", JsonSerializer.SerializeToElement(new { sample_rate = plan.Rate, bytes, headers }));
    }

    private static async IAsyncEnumerable<(string Kind, JsonElement Data)> ReadSse(
        Stream stream, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(stream, leaveOpen: true);
        var kind = "message";
        var lines = new List<string>();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (lines.Count > 0)
                {
                    using var document = JsonDocument.Parse(string.Join("\n", lines));
                    yield return (kind, document.RootElement.Clone());
                }
                lines.Clear();
                kind = "message";
                continue;
            }
            if (line.StartsWith(':'))
                continue;
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' '))
                value = value[1..];
            if (field == "event")
                kind = value;
            else if (field == "data")
                lines.Add(value);
        }
        cancellationToken.ThrowIfCancellationRequested();
        // An event is complete only after its blank-line delimiter, not at a truncated EOF.
    }

    private static Dictionary<string, JsonElement>? MergeOptions(
        Dictionary<string, JsonElement>? defaults, Dictionary<string, JsonElement>? overrides)
    {
        var values = new Dictionary<string, JsonElement>();
        var parameters = new Dictionary<string, JsonElement>();
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue(ProviderId, out var value) != true)
                continue;
            if (value.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.kalpalabs must be an object.");
            foreach (var property in value.EnumerateObject())
            {
                if (property.Name == "params" && property.Value.ValueKind == JsonValueKind.Object)
                {
                    foreach (var parameter in property.Value.EnumerateObject())
                        parameters[parameter.Name] = parameter.Value.Clone();
                    values["params"] = JsonSerializer.SerializeToElement(parameters);
                }
                else
                {
                    values[property.Name] = property.Value.Clone();
                    if (property.Name == "params")
                        parameters.Clear();
                }
            }
        }
        return values.Count == 0 ? null : new() { [ProviderId] = JsonSerializer.SerializeToElement(values) };
    }
}
