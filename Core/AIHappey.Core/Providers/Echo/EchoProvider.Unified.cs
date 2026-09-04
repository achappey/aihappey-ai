using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Echo;

public sealed partial class EchoProvider
{
    private const int DefaultChunkSize = 32;
    private const int DefaultChunkDelayMilliseconds = 25;
    private const int DefaultJitterMilliseconds = 10;
    private const int MaximumChunkSize = 65_536;
    private const int MaximumDelayMilliseconds = 60_000;

    public Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(CreateEchoResponse(request, ExtractLatestUserText(request)));
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var text = ExtractLatestUserText(request);
        var response = CreateEchoResponse(request, text);
        var options = ResolveStreamingOptions(request);
        var providerId = GetIdentifier();
        var responseId = request.Id ?? $"echo-{Guid.NewGuid():N}";
        var textId = $"{responseId}:text";
        var timestamp = DateTimeOffset.UtcNow;

        yield return CreateEchoEvent(providerId, textId, "text-start", new AITextStartEventData(), timestamp);

        foreach (var chunk in ChunkText(text, options.ChunkSize))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var delay = options.ChunkDelayMilliseconds;
            if (options.JitterMilliseconds > 0)
                delay += Random.Shared.Next(options.JitterMilliseconds + 1);

            if (delay > 0)
                await Task.Delay(delay, cancellationToken);

            yield return CreateEchoEvent(
                providerId,
                textId,
                "text-delta",
                new AITextDeltaEventData { Delta = chunk },
                timestamp);
        }

        yield return CreateEchoEvent(providerId, textId, "text-end", new AITextEndEventData(), timestamp);
        yield return new AIStreamEvent
        {
            ProviderId = providerId,
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = responseId,
                Timestamp = timestamp,
                Output = response.Output,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = response.Model,
                    CompletedAt = timestamp.ToUnixTimeSeconds(),
                    InputTokens = 0,
                    OutputTokens = 0,
                    TotalTokens = 0,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        response.Model ?? string.Empty,
                        timestamp,
                        response.Usage,
                        inputTokens: 0,
                        outputTokens: 0,
                        totalTokens: 0,
                        temperature: request.Temperature)
                }
            }
        };
    }

    private AIResponse CreateEchoResponse(AIRequest request, string text)
        => new()
        {
            ProviderId = GetIdentifier(),
            Model = request.Model,
            Status = "completed",
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content = [new AITextContentPart {
                            Type = "text",
                            Text = text }]
                    }
                ]
            },
            Usage = new AIUsage
            {
                InputTokens = 0,
                OutputTokens = 0,
                TotalTokens = 0
            }
        };

    private static string ExtractLatestUserText(AIRequest request)
    {
        var lastUser = request.Input?.Items?
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase));

        if (lastUser is not null)
            return string.Concat((lastUser.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text));

        return request.Input?.Text ?? string.Empty;
    }

    private EchoStreamingOptions ResolveStreamingOptions(AIRequest request)
    {
        var chunkSize = request.Metadata?.GetProviderOption<int?>(GetIdentifier(), "chunkSize")
                        ?? DefaultChunkSize;
        var chunkDelay = request.Metadata?.GetProviderOption<int?>(GetIdentifier(), "chunckDelay")
                         ?? DefaultChunkDelayMilliseconds;
        var jitter = request.Metadata?.GetProviderOption<int?>(GetIdentifier(), "jitter")
                     ?? DefaultJitterMilliseconds;

        return new EchoStreamingOptions(
            Math.Clamp(chunkSize, 1, MaximumChunkSize),
            Math.Clamp(chunkDelay, 0, MaximumDelayMilliseconds),
            Math.Clamp(jitter, 0, MaximumDelayMilliseconds));
    }

    private static IEnumerable<string> ChunkText(string text, int chunkSize)
    {
        for (var offset = 0; offset < text.Length; offset += chunkSize)
            yield return text.Substring(offset, Math.Min(chunkSize, text.Length - offset));
    }

    private static AIStreamEvent CreateEchoEvent(
        string providerId,
        string id,
        string type,
        object data,
        DateTimeOffset timestamp)
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

    private sealed record EchoStreamingOptions(
        int ChunkSize,
        int ChunkDelayMilliseconds,
        int JitterMilliseconds);
}
