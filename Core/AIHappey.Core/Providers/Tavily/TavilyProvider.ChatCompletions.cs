using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteResearchChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = options.Model;
        var input = BuildPromptFromCompletionMessages(options.Messages);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input derived from chat messages.");

        var outputSchema = TryExtractOutputSchema(options.ResponseFormat);

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, cancellationToken))
        {
            var update = ToChatCompletionUpdate(evt, model);
            if (update is not null)
                yield return update;
        }
    }

    private async Task<ChatCompletion> CompleteResearchChatAsync(
       ChatCompletionOptions options,
       CancellationToken cancellationToken)
    {
        var model = options.Model;
        var input = BuildPromptFromCompletionMessages(options.Messages);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input derived from chat messages.");

        var outputSchema = TryExtractOutputSchema(options.ResponseFormat);

        var queued = await QueueResearchTaskAsync(input, model, stream: false, outputSchema, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.RequestId, cancellationToken);

        return ToChatCompletion(completed, model);
    }

    private static ChatCompletion ToChatCompletion(TavilyCompletedTask completed, string model)
    {
        var text = ToOutputText(completed.Content);
        var metadata = BuildSourcesMetadata(completed.Sources) ?? [];

        return new ChatCompletion
        {
            Id = completed.RequestId,
            Created = ToUnixTime(completed.CreatedAt),
            Model = model,
            Choices =
            [
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = text,
                        sources = metadata.TryGetValue("sources", out var src) ? src : null
                    },
                    finish_reason = "stop"
                }
            ],
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0,
                response_time = completed.ResponseTime
            }
        };
    }

    private static ChatCompletionUpdate? ToChatCompletionUpdate(TavilyStreamEvent evt, string fallbackModel)
    {
        var delta = new Dictionary<string, object?>();

        if (evt.Delta is not null)
        {
            if (evt.Delta.Value.TryGetProperty("role", out var roleEl) && roleEl.ValueKind == JsonValueKind.String)
                delta["role"] = roleEl.GetString();

            if (!string.IsNullOrWhiteSpace(evt.ContentText))
                delta["content"] = evt.ContentText;
            else if (evt.ContentObject is not null)
                delta["content"] = JsonSerializer.Deserialize<object>(evt.ContentObject.Value.GetRawText(), JsonSerializerOptions.Web);

            if (evt.Delta.Value.TryGetProperty("tool_calls", out var toolCallsEl)
                && toolCallsEl.ValueKind == JsonValueKind.Object)
            {
                delta["tool_calls"] = JsonSerializer.Deserialize<object>(toolCallsEl.GetRawText(), JsonSerializerOptions.Web);
            }

            if (evt.Sources.Count > 0)
                delta["sources"] = evt.Sources.Select(ToSourceDto).ToList();
        }

        if (delta.Count == 0 && evt.Usage is null)
            return null;

        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["delta"] = delta
        };

        if (!string.IsNullOrWhiteSpace(evt.FinishReason))
            choice["finish_reason"] = evt.FinishReason;

        return new ChatCompletionUpdate
        {
            Id = evt.Id ?? Guid.NewGuid().ToString("n"),
            Created = evt.Created,
            Model = string.IsNullOrWhiteSpace(evt.Model) ? fallbackModel : evt.Model!,
            Choices = [choice],
            Usage = evt.Usage is null
                ? null
                : JsonSerializer.Deserialize<object>(evt.Usage.Value.GetRawText(), JsonSerializerOptions.Web)
        };
    }


}

