using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Rime;

public partial class RimeProvider
{
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Text is null || request.Text.Length == 0)
            throw new ArgumentException("At least one Rime speech text part is required.", nameof(request));
        if (request.Text.Any(part => part is null || part.Type != "text" || part.Text is null))
            throw new ArgumentException("Rime speech requires text parts with non-null text.", nameof(request));

        var text = string.Concat(request.Text.Select(part => part.Text));
        RimeSpeechPlan? plan = null;
        foreach (var part in request.Text)
        {
            if (part.Text.Length == 0)
                continue;
            var current = PrepareSpeech(new SpeechRequest
            {
                Model = request.Model, Text = text, Voice = part.Voice ?? request.Voice,
                OutputFormat = request.OutputFormat?.Type, Instructions = request.Instructions,
                // Speed is explicitly warned as unsupported by the shared preparation.
                Speed = (float?)request.Speed,
                Language = request.Language,
                ProviderOptions = MergeRimeOptions(request.ProviderOptions, part.ProviderOptions)
            }, request.OutputFormat?.Rate);
            if (plan is not null)
            {
                if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(plan.Payload, RimeSpeechJson),
                    JsonSerializer.SerializeToElement(current.Payload, RimeSpeechJson)))
                    throw new NotSupportedException("Rime HTTP speech accepts one effective voice and provider configuration per script; text-part changes are not supported.");
                plan.Warnings.AddRange(current.Warnings);
            }
            else
                plan = current;
        }
        if (plan is null)
            throw new ArgumentException("Rime speech text cannot be empty.", nameof(request));

        var includeRawChunks = request.IncludeRawChunks == true;
        cancellationToken.ThrowIfCancellationRequested();
        using var message = CreateSpeechMessage(plan);
        yield return new SpeechStreamStartPart
        {
            Warnings = plan.Warnings.DistinctBy(warning => JsonSerializer.Serialize(warning))
        };
        cancellationToken.ThrowIfCancellationRequested();
        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSpeechSuccessAsync(response, cancellationToken);
        var (mimeType, format, rate) = ResolveResponseAudio(response, plan);
        yield return new SpeechResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(ProviderId),
            Headers = response.Headers.Concat(response.Content.Headers)
                .ToDictionary(header => header.Key, header => string.Join(", ", header.Value), StringComparer.OrdinalIgnoreCase)
        };
        long bytes = 0;
        await foreach (var chunk in ReadSpeechChunksAsync(response, format, cancellationToken))
        {
            if (includeRawChunks)
                yield return new SpeechRawPart { RawValue = chunk.Raw };
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Audio is null)
                continue;
            bytes += chunk.ByteCount;
            yield return new SpeechAudioPart
            {
                Audio = chunk.Audio,
                AudioFormat = new AudioFormat { Type = mimeType, Rate = rate }
            };
        }
        cancellationToken.ThrowIfCancellationRequested();
        yield return new SpeechFinishPart { ProviderMetadata = CreateSpeechMetadata(plan, mimeType, rate, bytes) };
    }

    private static Dictionary<string, JsonElement>? MergeRimeOptions(
        Dictionary<string, JsonElement>? defaults, Dictionary<string, JsonElement>? overrides)
    {
        // Merge only our own namespace, without mutating either public request object.
        var values = new Dictionary<string, JsonElement>();
        foreach (var options in new[] { defaults, overrides })
        {
            if (options?.TryGetValue(ProviderId, out var rime) != true)
                continue;
            if (rime.ValueKind != JsonValueKind.Object)
                throw new ArgumentException("providerOptions.rime must be an object.");
            foreach (var property in rime.EnumerateObject())
                values[property.Name] = property.Value.Clone();
        }
        return values.Count == 0 ? null : new() { [ProviderId] = JsonSerializer.SerializeToElement(values) };
    }

    private sealed record RimeAudioChunk(string? Audio, string Raw, int ByteCount);

    private static async IAsyncEnumerable<RimeAudioChunk> ReadSpeechChunksAsync(
        HttpResponseMessage response, string format,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[16 * 1024 + 1];
        var pending = 0;
        var receivedAudio = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(buffer.AsMemory(pending, buffer.Length - pending), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (read == 0)
                break;
            receivedAudio = true;
            var raw = Convert.ToBase64String(buffer, pending, read);
            var available = pending + read;
            var whole = format == "pcm" ? available - available % 2 : available;
            var audio = whole == 0 ? null : Convert.ToBase64String(buffer, 0, whole);
            pending = available - whole;
            if (pending != 0)
                buffer[0] = buffer[whole];
            yield return new(audio, raw, whole);
        }
        if (!receivedAudio || pending != 0)
            throw new InvalidOperationException("Rime returned empty or incomplete audio.");
    }
}
