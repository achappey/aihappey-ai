using System.Runtime.CompilerServices;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    private async Task<ChatCompletion> CompleteKirhaChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Kirha search requires text from the last user message.");

        var result = await ExecuteKirhaSearchAsync(options.Model, query, passthrough: null, cancellationToken);

        return new ChatCompletion
        {
            Id = result.Response.Id ?? Guid.NewGuid().ToString("n"),
            Created = UnixNow(),
            Model = options.Model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = result.Summary,
                        metadata = result.Metadata,
                        reasoning = result.ReasoningItems.Select(r => new
                        {
                            id = r.Id,
                            text = r.Text,
                            metadata = r.Metadata
                        }).ToArray(),
                        tool_calls = result.ToolCalls.Select(t => new
                        {
                            id = t.Id,
                            type = "function",
                            function = new
                            {
                                name = t.ToolName,
                                arguments = System.Text.Json.JsonSerializer.Serialize(t.Input, Json)
                            },
                            provider_executed = t.ProviderExecuted,
                            output = t.Output,
                            metadata = t.Metadata
                        }).ToArray()
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = BuildKirhaUsage(result.Response.Usage)
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteKirhaChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteKirhaChatAsync(options, cancellationToken);

        yield return new ChatCompletionUpdate
        {
            Id = completion.Id,
            Created = completion.Created,
            Model = completion.Model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ],
            Usage = null
        };

        var firstChoice = completion.Choices.FirstOrDefault();
        if (firstChoice is not null)
        {
            var json = System.Text.Json.JsonSerializer.SerializeToElement(firstChoice, Json);
            if (json.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                var text = content.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return new ChatCompletionUpdate
                    {
                        Id = completion.Id,
                        Created = completion.Created,
                        Model = completion.Model,
                        Choices =
                        [
                            new { index = 0, delta = new { content = text }, finish_reason = (string?)null }
                        ],
                        Usage = null
                    };
                }
            }
        }

        yield return new ChatCompletionUpdate
        {
            Id = completion.Id,
            Created = completion.Created,
            Model = completion.Model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ],
            Usage = completion.Usage
        };
    }
}
