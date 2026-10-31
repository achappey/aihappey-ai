using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Core.AI;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpperAI;

public partial class OpperAIProvider
{
    private const int SpeechChunkBytes = 48 * 1024; // multiple of 3 so every chunk is standalone base64

    private static readonly string[] StreamingCapabilityNames =
        ["stream", "streaming", "supports_streaming", "supportsstreaming", "stream_supported", "streamsupported"];

    /// <summary>
    /// Opper's /v3/audio/speech only answers once with the complete audio (inline base64 or bytes),
    /// so there is no native audio stream: the buffered response is decoded and chunked.
    /// </summary>
    public async IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (request.Text is null || request.Text.Length == 0 || request.Text.All(part => string.IsNullOrEmpty(part?.Text)))
            throw new ArgumentException("Text is required.", nameof(request));

        yield return new SpeechStreamStartPart();

        var first = true;
        decimal totalCost = 0;
        var hasCost = false;
        long characters = 0;
        var hasCharacters = false;

        foreach (var group in GroupOpperAISpeechParts(request))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await OpperAISpeechRequest(new SpeechRequest
            {
                Model = request.Model,
                Text = group.Text,
                Voice = group.Voice,
                OutputFormat = request.OutputFormat?.Type,
                Instructions = request.Instructions,
                Speed = request.Speed,
                Language = request.Language,
                ProviderOptions = group.ProviderOptions
            }, cancellationToken);

            if (first)
            {
                first = false;
                yield return new SpeechResponseMetadataPart
                {
                    Timestamp = response.Response.Timestamp,
                    ModelId = response.Response.ModelId,
                    Headers = response.Response.Headers
                };
            }

            if (request.IncludeRawChunks == true)
                yield return new SpeechRawPart { RawValue = response.Response.Body };

            var bytes = Convert.FromBase64String(response.Audio.Base64);
            var format = new AudioFormat { Type = response.Audio.Format };

            for (var offset = 0; offset < bytes.Length; offset += SpeechChunkBytes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new SpeechAudioPart
                {
                    Audio = Convert.ToBase64String(bytes, offset, Math.Min(SpeechChunkBytes, bytes.Length - offset)),
                    AudioFormat = format,
                    ProviderMetadata = response.ProviderMetadata
                };
            }

            if (response.Response.Body is JsonElement body
                && TryGetOpperAIProperty(body, "usage", out var usage)
                && usage.ValueKind == JsonValueKind.Object)
            {
                if (TryGetOpperAIDouble(usage, "cost") is { } cost)
                {
                    totalCost += (decimal)cost;
                    hasCost = true;
                }

                if (TryGetOpperAIDouble(usage, "characters") is { } chars)
                {
                    characters += (long)chars;
                    hasCharacters = true;
                }
            }
        }

        yield return new SpeechFinishPart
        {
            Usage = hasCharacters ? new StreamingSpeechUsage { InputCharacters = characters } : null,
            ProviderMetadata = GetIdentifier().CreatePrimitiveProviderMetadata(null, hasCost ? totalCost : null)
        };
    }

    public async IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Audio))
            throw new ArgumentException("Audio is required.", nameof(request));
        ArgumentNullException.ThrowIfNull(request.InputAudioFormat);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.InputAudioFormat.Type);

        var transcription = new TranscriptionRequest
        {
            Model = request.Model,
            Audio = request.Audio,
            MediaType = ResolveOpperAIAudioMediaType(request.InputAudioFormat.Type),
            ProviderOptions = request.ProviderOptions
        };

        ApplyAuthHeader();

        // Only an explicit "not streaming" from /v3/audio/models selects the buffered path.
        if (await GetOpperAIAudioModelStreamingAsync(request.Model, cancellationToken) == false)
        {
            await foreach (var part in BufferedOpperAITranscriptionAsync(request, transcription, cancellationToken))
                yield return part;
            yield break;
        }

        var payload = BuildOpperAITranscriptionPayload(transcription, GetOpperAIProviderOptions(request.ProviderOptions));
        payload["stream"] = true;
        payload.Remove("async");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "v3/audio/transcriptions")
        {
            Content = CreateOpperAIJsonContent(payload)
        };
        httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(raw)
                ? $"OpperAI transcription failed ({(int)response.StatusCode})."
                : $"OpperAI transcription failed ({(int)response.StatusCode}): {raw}");
        }

        yield return new TranscriptionStreamStartPart();
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = DateTime.UtcNow,
            ModelId = request.Model.ToModelId(GetIdentifier()),
            Headers = response.GetHeaders()
        };

        var text = new StringBuilder();
        string? finalText = null;
        string? language = null;
        double? duration = null;
        JsonElement? usage = null;
        var segments = new List<TranscriptionSegment>();
        var completed = false;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        await foreach (var (eventName, data) in ReadOpperAISseAsync(reader, cancellationToken))
        {
            if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                break;

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement.Clone();

            if (request.IncludeRawChunks == true)
                yield return new TranscriptionRawPart { RawValue = root };

            var type = TryGetOpperAIString(root, "type") ?? eventName;

            if (string.Equals(type, "error", StringComparison.OrdinalIgnoreCase)
                || (type is null && TryGetOpperAIProperty(root, "error", out _)))
            {
                throw new InvalidOperationException($"OpperAI transcription failed: {ExtractOpperAIErrorMessage(root)}");
            }

            if (TryGetOpperAIProperty(root, "usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
                usage = usageElement.Clone();

            if (string.Equals(type, "transcript.text.delta", StringComparison.OrdinalIgnoreCase))
            {
                var delta = TryGetOpperAIString(root, "delta", "text") ?? string.Empty;
                if (delta.Length == 0)
                    continue;

                text.Append(delta);
                yield return new TranscriptionDeltaPart { Delta = delta };
            }
            else if (string.Equals(type, "transcript.text.done", StringComparison.OrdinalIgnoreCase))
            {
                completed = true;
                finalText = TryGetOpperAIString(root, "text");
                language = TryGetOpperAIString(root, "language");
                duration = TryGetOpperAIDouble(root, "duration", "durationInSeconds")
                    ?? (usage is { } doneUsage ? TryGetOpperAIDouble(doneUsage, "seconds") : null);
                segments.AddRange(ExtractOpperAITranscriptionSegments(root));
            }
        }

        if (!completed)
            throw new InvalidOperationException("OpperAI transcription stream ended before transcript.text.done.");

        var result = finalText ?? text.ToString();
        var metadata = CreateOpperAIStreamingTranscriptionMetadata(usage);

        yield return new TranscriptionFinalPart { Text = result, ProviderMetadata = metadata };
        yield return new TranscriptionFinishPart
        {
            Text = result,
            Segments = segments,
            Language = language,
            DurationInSeconds = duration,
            ProviderMetadata = metadata
        };
    }

    private async IAsyncEnumerable<StreamingTranscriptionPart> BufferedOpperAITranscriptionAsync(
        StreamingTranscriptionRequest request,
        TranscriptionRequest transcription,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var response = await OpperAITranscriptionRequest(transcription, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        yield return new TranscriptionStreamStartPart { Warnings = response.Warnings };
        yield return new TranscriptionResponseMetadataPart
        {
            Timestamp = response.Response.Timestamp,
            ModelId = response.Response.ModelId,
            Headers = response.Response.Headers,
            Body = response.Response.Body
        };

        if (request.IncludeRawChunks == true)
            yield return new TranscriptionRawPart { RawValue = response.Response.Body };
        if (response.Text.Length > 0)
            yield return new TranscriptionDeltaPart { Delta = response.Text };

        yield return new TranscriptionFinalPart { Text = response.Text, ProviderMetadata = response.ProviderMetadata };
        yield return new TranscriptionFinishPart
        {
            Text = response.Text,
            Segments = response.Segments,
            Language = response.Language,
            DurationInSeconds = response.DurationInSeconds,
            ProviderMetadata = response.ProviderMetadata
        };
    }

    private Dictionary<string, JsonElement> CreateOpperAIStreamingTranscriptionMetadata(JsonElement? usage)
    {
        if (usage is not { } value)
            return CreateOpperAIMediaMetadata(null);

        var cost = TryGetOpperAIDouble(value, "cost") is { } parsed ? (decimal?)parsed : null;
        return GetIdentifier().CreatePrimitiveProviderMetadata(new { usage = value }, cost);
    }

    private static string ExtractOpperAIErrorMessage(JsonElement root)
    {
        if (TryGetOpperAIProperty(root, "error", out var error))
        {
            if (error.ValueKind == JsonValueKind.String)
                return error.GetString() ?? string.Empty;
            if (TryGetOpperAIString(error, "message") is { } message)
                return message;
        }

        return TryGetOpperAIString(root, "message") ?? root.GetRawText();
    }

    private static async IAsyncEnumerable<(string? EventName, string Data)> ReadOpperAISseAsync(
        StreamReader reader,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? eventName = null;
        var data = new StringBuilder();

        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length == 0)
            {
                if (data.Length > 0)
                    yield return (eventName, data.ToString());

                eventName = null;
                data.Clear();
                continue;
            }

            if (line[0] == ':')
                continue;

            if (line.StartsWith("event:", StringComparison.Ordinal))
                eventName = line[6..].Trim();
            else if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                if (data.Length > 0)
                    data.Append('\n');
                data.Append(line[5..].TrimStart());
            }
        }

        if (data.Length > 0)
            yield return (eventName, data.ToString());
    }

    private async Task<bool?> GetOpperAIAudioModelStreamingAsync(string model, CancellationToken cancellationToken)
    {
        Dictionary<string, bool?> capabilities;
        try
        {
            capabilities = await _memoryCache.GetOrCreateAsync(
                $"{this.GetCacheKey()}:audio-streaming",
                async ct =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, "v3/audio/models");
                    using var resp = await _client.SendAsync(req, ct);
                    resp.EnsureSuccessStatusCode();

                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    return ParseOpperAIAudioStreamingCapabilities(doc.RootElement);
                },
                baseTtl: TimeSpan.FromHours(1),
                jitterMinutes: 30,
                cancellationToken: cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        var id = model.StartsWith(GetIdentifier() + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(GetIdentifier().Length + 1)..]
            : model;

        return capabilities.TryGetValue(id, out var streaming) ? streaming : null;
    }

    internal static Dictionary<string, bool?> ParseOpperAIAudioStreamingCapabilities(JsonElement root)
    {
        var result = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);

        if (!TryGetOpperAIProperty(root, "models", out var models) || models.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in models.EnumerateArray())
        {
            if (TryGetOpperAIString(entry, "id") is not { Length: > 0 } id)
                continue;

            result[id] = FindOpperAIStreamingFlag(entry, 0);
        }

        return result;
    }

    private static bool? FindOpperAIStreamingFlag(JsonElement element, int depth)
    {
        if (depth > 4)
            return null;

        if (element.ValueKind == JsonValueKind.Array)
        {
            if (element.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String
                && StreamingCapabilityNames.Contains(item.GetString()?.Trim().ToLowerInvariant() ?? string.Empty)))
            {
                return true;
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.Object)
            return null;

        bool? nested = null;
        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.ToLowerInvariant();
            if (StreamingCapabilityNames.Contains(name)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.Value.GetBoolean();
            }

            if (name is "parameters" or "pricing")
                continue;

            if (FindOpperAIStreamingFlag(property.Value, depth + 1) is { } found)
                nested = (nested ?? false) || found;
        }

        return nested;
    }

    private static string ResolveOpperAIAudioMediaType(string format)
        => format.Contains('/')
            ? format
            : format.ToLowerInvariant() switch
            {
                "mp3" or "mpeg" => "audio/mpeg",
                "wav" or "wave" => "audio/wav",
                "m4a" or "mp4" => "audio/mp4",
                var other => "audio/" + other
            };

    private sealed record OpperAISpeechGroup(string Text, string? Voice, Dictionary<string, JsonElement>? ProviderOptions);

    // Consecutive parts with the same voice form one script; a voice change starts a new request.
    private IEnumerable<OpperAISpeechGroup> GroupOpperAISpeechParts(StreamingSpeechRequest request)
    {
        var groups = new List<OpperAISpeechGroup>();
        var text = new StringBuilder();
        string? voice = null;
        Dictionary<string, JsonElement>? options = null;
        var open = false;

        void Flush()
        {
            if (open && text.Length > 0)
                groups.Add(new OpperAISpeechGroup(text.ToString(), voice, options));

            text.Clear();
            open = false;
            options = null;
        }

        foreach (var part in request.Text)
        {
            if (string.IsNullOrEmpty(part?.Text))
                continue;

            var partVoice = string.IsNullOrWhiteSpace(part.Voice) ? request.Voice : part.Voice;
            if (open && !string.Equals(voice, partVoice, StringComparison.Ordinal))
                Flush();

            voice = partVoice;
            open = true;
            text.Append(part.Text);
            options = MergeOpperAIProviderOptions(options ?? request.ProviderOptions, part.ProviderOptions);
        }

        Flush();
        return groups;
    }

    private Dictionary<string, JsonElement>? MergeOpperAIProviderOptions(
        Dictionary<string, JsonElement>? baseOptions,
        Dictionary<string, JsonElement>? overrides)
    {
        if (overrides is null || !overrides.TryGetValue(GetIdentifier(), out var overrideElement)
            || overrideElement.ValueKind != JsonValueKind.Object)
        {
            return baseOptions;
        }

        var merged = new Dictionary<string, JsonElement>(baseOptions ?? [], StringComparer.Ordinal);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        if (merged.TryGetValue(GetIdentifier(), out var existing) && existing.ValueKind == JsonValueKind.Object)
            foreach (var property in existing.EnumerateObject())
                values[property.Name] = property.Value.Clone();

        foreach (var property in overrideElement.EnumerateObject())
            values[property.Name] = property.Value.Clone();

        merged[GetIdentifier()] = JsonSerializer.SerializeToElement(values);
        return merged;
    }
}
