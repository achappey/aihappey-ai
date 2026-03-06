using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Valyu;

public partial class ValyuProvider
{
    private async Task<ChatCompletion> CompleteValyuChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsAnswerModel(model))
            return await CompleteValyuAnswerChatAsync(options, cancellationToken);

        if (IsDeepResearchModel(model))
            return await CompleteValyuDeepResearchChatAsync(options, cancellationToken);

        throw new NotSupportedException($"Valyu model '{model}' is not supported for chat completions.");
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteValyuChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var completion = await CompleteValyuChatAsync(options, cancellationToken);
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

    private async Task<ChatCompletion> CompleteValyuAnswerChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Valyu answer requires non-empty input derived from chat messages.");

        var searchType = ResolveAnswerSearchType(options.Model);
        var result = await ExecuteAnswerAsync(query, searchType, passthrough: null, cancellationToken);
        var text = result.Text;
        var metadata = result.Metadata;

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
            Usage = BuildAnswerUsage(metadata)
        };
    }

    private async Task<ChatCompletion> CompleteValyuDeepResearchChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken)
    {
        var query = BuildPromptFromCompletionMessages(options.Messages);
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Valyu deep research requires non-empty input derived from chat messages.");

        var mode = ResolveDeepResearchMode(options.Model);
        var result = await ExecuteDeepResearchAsync(query, mode, passthrough: null, downloadArtifacts: false, cancellationToken);
        var text = result.Text;

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
                        metadata = result.Metadata
                    },
                    finish_reason = result.IsSuccess ? "stop" : "error"
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            }
        };
    }

    private static object BuildAnswerUsage(Dictionary<string, object?>? metadata)
    {
        var usage = ExtractUsageFromMetadata(metadata);
        return new
        {
            prompt_tokens = usage.InputTokens,
            completion_tokens = usage.OutputTokens,
            total_tokens = usage.TotalTokens
        };
    }
}

