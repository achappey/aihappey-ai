using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;

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
        content = first?.Message.Content ?? string.Empty;

        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = model,
            Choices =
            [
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Role = "assistant" }, FinishReason = (string?)null }
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
                    new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta { Content = content }, FinishReason = (string?)null }
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
                new ChatCompletionStreamChoice { Index = 0, Delta = new ChatCompletionDelta {  }, FinishReason = "stop" }
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
                new ChatCompletionChoice { Index = 0, Message = new ChatCompletionResponseMessage { Role = "assistant", Content = text, AdditionalProperties = new() { ["metadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web) } }, FinishReason = "stop" }
            ],
            Usage = (BuildAnswerUsage(metadata)).ToChatCompletionUsage()
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
                new ChatCompletionChoice { Index = 0, Message = new ChatCompletionResponseMessage { Role = "assistant", Content = text, AdditionalProperties = new() { ["metadata"] = JsonSerializer.SerializeToElement(result.Metadata, JsonSerializerOptions.Web) } }, FinishReason = result.IsSuccess ? "stop" : "error" }
            ],
            Usage = new ChatCompletionUsage { PromptTokens = 0, CompletionTokens = 0, TotalTokens = 0 }
        };
    }

    private static object BuildAnswerUsage(Dictionary<string, object?>? metadata)
    {
        var usage = ExtractUsageFromMetadata(metadata);
        return new ChatCompletionUsage { PromptTokens = usage.InputTokens, CompletionTokens = usage.OutputTokens, TotalTokens = usage.TotalTokens };
    }
}

