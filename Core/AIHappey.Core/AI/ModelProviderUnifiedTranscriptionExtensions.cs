using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.AI;

/// <summary>
/// Adapts an OpenAI-compatible native transcription stream into the unified
/// conversation stream consumed by every conversation endpoint.
/// </summary>
public static class ModelProviderUnifiedTranscriptionExtensions
{
    public static async Task<AIResponse> ExecuteUnifiedTranscriptionAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var audio = GetLastUserAudio(request);
        var transcriptionRequest = new OpenAITranscriptionRequest
        {
            Model = GetProviderModelId(model, modelProvider.GetIdentifier()),
            File = CreateAudioFormFile(audio),
            Stream = false
        };

        var transcription = await modelProvider.OpenAITranscriptionRequestAsync(
            transcriptionRequest,
            cancellationToken);

        return new AIResponse
        {
            ProviderId = modelProvider.GetIdentifier(),
            Model = model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = transcription.Text
                            }
                        ]
                    }
                ]
            }
        };
    }

    public static async IAsyncEnumerable<AIStreamEvent> StreamUnifiedTranscriptionAsync(
        this IModelProvider modelProvider,
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(modelProvider);
        ArgumentNullException.ThrowIfNull(request);

        var model = request.Model;
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var audio = GetLastUserAudio(request);
        var streamId = request.Id ?? Guid.NewGuid().ToString("N");
        var timestamp = DateTimeOffset.UtcNow;
        var providerId = modelProvider.GetIdentifier();
        var textStarted = false;
        var receivedTextDelta = false;

        yield return CreateEvent(
            providerId,
            "text-start",
            streamId,
            timestamp,
            new AITextStartEventData());
        textStarted = true;

        var transcriptionRequest = new OpenAITranscriptionRequest
        {
            Model = GetProviderModelId(model, providerId),
            File = CreateAudioFormFile(audio),
            Stream = true
        };

        await foreach (var transcriptionEvent in modelProvider
            .OpenAITranscriptionStreamingAsync(transcriptionRequest, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            switch (transcriptionEvent)
            {
                case OpenAITranscriptionTextDelta { Delta: { Length: > 0 } delta }:
                    receivedTextDelta = true;
                    yield return CreateEvent(
                        providerId,
                        "text-delta",
                        streamId,
                        DateTimeOffset.UtcNow,
                        new AITextDeltaEventData { Delta = delta });
                    break;

                case OpenAITranscriptionTextDone { Text: { Length: > 0 } text }:
                    // A compliant native stream sends deltas before its final done
                    // event. Only emit done text when no delta was received, avoiding
                    // duplicated transcript output for providers that follow that contract.
                    if (!receivedTextDelta)
                    {
                        yield return CreateEvent(
                            providerId,
                            "text-delta",
                            streamId,
                            DateTimeOffset.UtcNow,
                            new AITextDeltaEventData { Delta = text });
                    }

                    break;
            }
        }

        if (textStarted)
        {
            yield return CreateEvent(
                providerId,
                "text-end",
                streamId,
                DateTimeOffset.UtcNow,
                new AITextEndEventData());
        }

        yield return CreateEvent(
            providerId,
            "finish",
            streamId,
            DateTimeOffset.UtcNow,
            new AIFinishEventData
            {
                FinishReason = "stop",
                Model = model
            });
    }

    private static AIFileContentPart GetLastUserAudio(AIRequest request)
    {
        var lastUserMessage = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

        var audio = lastUserMessage?.Content?
            .OfType<AIFileContentPart>()
            .LastOrDefault(IsAudioFile);

        if (audio is null)
        {
            throw new ArgumentException(
                "Streaming transcription conversation requests require an audio file in the last user message.",
                nameof(request));
        }

        if (!TryGetAudioBytes(audio.Data, out _))
        {
            throw new ArgumentException(
                "The audio file in the last user message must contain valid base64 audio data.",
                nameof(request));
        }

        return audio;
    }

    private static bool IsAudioFile(AIFileContentPart file)
        => !string.IsNullOrWhiteSpace(file.MediaType)
           && file.MediaType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
           && file.Data is not null;

    private static IFormFile CreateAudioFormFile(AIFileContentPart audio)
    {
        if (!TryGetAudioBytes(audio.Data, out var bytes))
            throw new InvalidOperationException("Validated audio data could not be read.");

        var mediaType = audio.MediaType!;
        var filename = string.IsNullOrWhiteSpace(audio.Filename)
            ? "audio" + GetFileExtension(mediaType)
            : audio.Filename;

        return new FormFile(new MemoryStream(bytes, writable: false), 0, bytes.Length, "file", filename)
        {
            Headers = new HeaderDictionary(),
            ContentType = mediaType
        };
    }

    private static bool TryGetAudioBytes(object? data, out byte[] bytes)
    {
        bytes = [];
        var value = data?.ToString();
        if (string.IsNullOrWhiteSpace(value))
            return false;

        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var marker = value.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            if (marker < 0)
                return false;

            value = value[(marker + ";base64,".Length)..];
        }

        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string GetProviderModelId(string model, string providerId)
    {
        var split = model.SplitModelId();
        return string.Equals(split.Provider, providerId, StringComparison.OrdinalIgnoreCase)
               && !string.IsNullOrWhiteSpace(split.Model)
            ? split.Model
            : model;
    }

    private static string GetFileExtension(string mediaType)
        => mediaType.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp4" => ".mp4",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            _ => ".audio"
        };

    private static AIStreamEvent CreateEvent(
        string providerId,
        string type,
        string id,
        DateTimeOffset timestamp,
        object data)
        => new()
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = id,
                Timestamp = timestamp,
                Data = data
            }
        };
}
