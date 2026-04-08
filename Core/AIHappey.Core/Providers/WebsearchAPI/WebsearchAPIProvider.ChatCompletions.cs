using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

namespace AIHappey.Core.Providers.WebsearchAPI;

public partial class WebsearchAPIProvider
{
    private async Task<ChatCompletion> CompleteWebSearchChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("WebsearchAPI requires non-empty input derived from chat messages.");

        var result = await ExecuteAiSearchAsync(query, passthrough: null, cancellationToken);
        var text = BuildAnswerWithSourceMarkdown(result);
        var metadata = BuildResultMetadata(result);

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
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
                        content = text,
                        metadata
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0,
                response_time = result.ResponseTime
            }
        };
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteWebSearchChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteWebSearchChatAsync(options, cancellationToken);
        var id = completion.Id;
        var created = completion.Created;
        var model = completion.Model;

        string content = string.Empty;
        var first = completion.Choices.FirstOrDefault();
        if (first is JsonElement el && el.ValueKind == JsonValueKind.Object
            && el.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.Object
            && msgEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
        {
            content = contentEl.GetString() ?? string.Empty;
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            ],
            Usage = null
        };

        if (!string.IsNullOrWhiteSpace(content))
        {
            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = model,
                Choices =
                [
                    new { index = 0, delta = new { content }, finish_reason = (string?)null }
                ],
                Usage = null
            };
        }

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new { index = 0, delta = new { }, finish_reason = "stop" }
            ],
            Usage = completion.Usage
        };
    }
}

