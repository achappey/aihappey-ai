using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.XVoice;

public partial class XVoiceProvider
{
    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        var request = await CreateOpenAITranscriptionRequest(options, false, cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return new OpenAITranscriptionResponse
        {
            Text = response.Text,
            Usage = response.DurationInSeconds is { } seconds
                ? new OpenAITranscriptionDurationUsage { Seconds = seconds } : null
        };
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = await CreateOpenAITranscriptionRequest(options, true, cancellationToken);
        var nativeOptions = TranscriptionOptions(request.ProviderOptions);
        using var message = CreateTranscriptionMessage(request.Model, DecodeTranscriptionFile(request), request.MediaType, nativeOptions);
        ApplyAuthHeader();
        using var response = await SendAsync(message, cancellationToken);
        await foreach (var item in ReadEventsAsync(response, "transcript.text.done", cancellationToken))
        {
            if (TranscriptionString(item, "type") == "transcript.text.delta")
            {
                var delta = RequiredTranscriptionString(item, "delta");
                yield return new OpenAITranscriptionTextDelta { Delta = delta };
            }
            if (TranscriptionString(item, "type") == "transcript.text.done")
                yield return new OpenAITranscriptionTextDone
                {
                    Text = RequiredTranscriptionString(item, "text"),
                    Usage = item.TryGetProperty("usage", out var usage) ? usage.Clone() : null
                };
        }
    }

    private static async Task<TranscriptionRequest> CreateOpenAITranscriptionRequest(
        OpenAITranscriptionRequest options, bool stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        cancellationToken.ThrowIfCancellationRequested();
        var native = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (options.AdditionalProperties is { } additional)
        {
            if (additional.TryGetValue("providerOptions", out var providers)
                && providers.ValueKind == JsonValueKind.Object
                && providers.TryGetProperty(ProviderId, out var provider))
                native = TranscriptionOptions(new() { [ProviderId] = provider });
            foreach (var field in additional.Where(p => p.Key != "providerOptions"))
                native[field.Key] = field.Value.Clone();
        }
        if (options.Language is not null)
            native["language"] = JsonSerializer.SerializeToElement(options.Language);
        if (options.Prompt is not null)
            native["prompt"] = JsonSerializer.SerializeToElement(options.Prompt);
        if (options.ResponseFormat is not null)
            native["response_format"] = JsonSerializer.SerializeToElement(options.ResponseFormat);
        if (options.Temperature is not null)
            native["temperature"] = JsonSerializer.SerializeToElement(options.Temperature);
        if (options.TimestampGranularities is not null)
            native["timestamp_granularities"] = JsonSerializer.SerializeToElement(options.TimestampGranularities);
        if (options.Include is not null)
            native["include"] = JsonSerializer.SerializeToElement(options.Include);
        if (options.ChunkingStrategy is not null)
            native["chunking_strategy"] = JsonSerializer.SerializeToElement(options.ChunkingStrategy);
        if (options.KnownSpeakerNames is not null)
            native["known_speaker_names"] = JsonSerializer.SerializeToElement(options.KnownSpeakerNames);
        if (options.KnownSpeakerReferences is not null)
            native["known_speaker_references"] = JsonSerializer.SerializeToElement(options.KnownSpeakerReferences);
        native["stream"] = JsonSerializer.SerializeToElement(stream);
        TranscriptionResponseFormat(native);
        var request = await options.ToTranscriptionRequest(options.Model, ProviderId, cancellationToken);
        if (string.IsNullOrWhiteSpace(options.File.ContentType)
            || options.File.ContentType == "application/octet-stream")
        {
            request.MediaType = Path.GetExtension(options.File.FileName).ToLowerInvariant() switch
            {
                ".flac" => "audio/flac", ".m4a" => "audio/mp4", ".mp3" => "audio/mpeg",
                ".mp4" => "video/mp4", ".ogg" => "audio/ogg", ".wav" => "audio/wav",
                ".webm" => "audio/webm",
                _ => request.MediaType
            };
        }
        request.ProviderOptions = new() { [ProviderId] = JsonSerializer.SerializeToElement(native) };
        return request;
    }
}
