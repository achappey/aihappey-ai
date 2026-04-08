using AIHappey.ChatCompletions.Models;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.OrqAgentRuntime;

public partial class OrqAgentRuntimeProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ApplyAuthHeader();

        var response = await InvokeInternalAsync(BuildInvokeRequest(options, stream: false), cancellationToken);
        return ToChatCompletion(options, response);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        ApplyAuthHeader();

        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var emittedText = new Dictionary<string, string>(StringComparer.Ordinal);
        var emittedToolArguments = new Dictionary<string, string>(StringComparer.Ordinal);
        object? usage = null;
        string finishReason = "stop";
        bool emittedTerminal = false;

        await foreach (var chunk in InvokeStreamingInternalAsync(BuildInvokeRequest(options, stream: true), cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(chunk.Id))
                responseId = chunk.Id!;

            if (!string.IsNullOrWhiteSpace(chunk.Created))
                createdAt = ParseUnixTime(chunk.Created, createdAt);

            if (TryConvertJsonElement(chunk.Usage, out var usageObject))
                usage = usageObject;

            foreach (var choice in chunk.Choices ?? [])
            {
                var choiceIndex = choice.Index;
                var text = ExtractMessageText(choice.Message);
                var textDelta = GetIncrementalDelta(emittedText, $"choice:{choiceIndex}:text", text);

                if (!string.IsNullOrEmpty(textDelta))
                {
                    yield return new ChatCompletionUpdate
                    {
                        Id = responseId,
                        Created = createdAt,
                        Model = options.Model,
                        Choices =
                        [
                            new
                            {
                                index = choiceIndex,
                                delta = new { content = textDelta }
                            }
                        ]
                    };
                }

                var toolCallDeltas = BuildStreamingToolCalls(choice.Message, emittedToolArguments);
                if (toolCallDeltas.Count > 0)
                {
                    yield return new ChatCompletionUpdate
                    {
                        Id = responseId,
                        Created = createdAt,
                        Model = options.Model,
                        Choices =
                        [
                            new
                            {
                                index = choiceIndex,
                                delta = new { tool_calls = toolCallDeltas }
                            }
                        ]
                    };
                }

                if (!string.IsNullOrWhiteSpace(choice.FinishReason))
                    finishReason = choice.FinishReason!;
                else if (choice.Message?.ToolCalls?.Count > 0)
                    finishReason = "tool_calls";
            }

            if (chunk.IsFinal || HasTerminalChoice(chunk))
            {
                finishReason = DetermineFinishReason(chunk, finishReason);

                yield return new ChatCompletionUpdate
                {
                    Id = responseId,
                    Created = chunk.Finalized is null
                        ? DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        : ParseUnixTime(chunk.Finalized, DateTimeOffset.UtcNow.ToUnixTimeSeconds()),
                    Model = options.Model,
                    Choices =
                    [
                        new
                        {
                            index = 0,
                            delta = new { },
                            finish_reason = finishReason
                        }
                    ],
                    Usage = usage
                };

                emittedTerminal = true;
            }
        }

        if (!emittedTerminal)
        {
            yield return new ChatCompletionUpdate
            {
                Id = responseId,
                Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Model = options.Model,
                Choices =
                [
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = finishReason
                    }
                ],
                Usage = usage
            };
        }
    }
}
