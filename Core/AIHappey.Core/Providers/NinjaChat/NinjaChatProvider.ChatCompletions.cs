using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider
{
    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
        {
            var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return ToNativeSearchChatCompletion(result, options.Model ?? NativeSearchModelId);
        }

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "v1/chat",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(options.Model))
            return CompleteNativeSearchStreamingAsync(options, cancellationToken);

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "v1/chat",
                    cancellationToken: cancellationToken);
    }

    private async IAsyncEnumerable<ChatCompletionUpdate> CompleteNativeSearchStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var state = new NinjaChatSearchChatCompletionStreamingState();
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var streamEvent in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            var update = ToNativeSearchChatCompletionUpdate(streamEvent, options.Model ?? NativeSearchModelId, state);
            if (update is not null)
                yield return update;
        }
    }

    private static ChatCompletion ToNativeSearchChatCompletion(AIResponse response, string fallbackModel)
    {
        var metadata = response.Metadata ?? [];
        var sources = ExtractNativeSearchSourceDtos(response.Output).ToList();
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = ExtractNativeSearchOutputText(response.Output)
        };

        if (sources.Count > 0)
            message["sources"] = sources;

        return new ChatCompletion
        {
            Id = metadata.TryGetValue("chatcompletions.response.id", out var id) ? id?.ToString() ?? $"chatcmpl_{Guid.NewGuid():N}" : $"chatcmpl_{Guid.NewGuid():N}",
            Object = "chat.completion",
            Created = metadata.TryGetValue("chatcompletions.response.created", out var created)
                && long.TryParse(created?.ToString(), out var createdAt)
                    ? createdAt
                    : DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = response.Model ?? fallbackModel,
            Choices =
            [
                new
                {
                    index = 0,
                    message,
                    finish_reason = "stop"
                }
            ],
            Usage = response.Usage
        };
    }

    private static ChatCompletionUpdate? ToNativeSearchChatCompletionUpdate(
        AIStreamEvent streamEvent,
        string fallbackModel,
        NinjaChatSearchChatCompletionStreamingState state)
    {
        var delta = new Dictionary<string, object?>();
        object? usage = null;
        string? finishReason = null;

        switch (streamEvent.Event.Type)
        {
            case "text-start":
                if (state.RoleEmitted)
                    return null;

                state.RoleEmitted = true;
                delta["role"] = "assistant";
                break;

            case "text-delta":
                if (!state.RoleEmitted)
                {
                    state.RoleEmitted = true;
                    delta["role"] = "assistant";
                }

                delta["content"] = (streamEvent.Event.Data as AITextDeltaEventData)?.Delta ?? string.Empty;
                break;

            case "source-url":
                delta["sources"] = new List<object> { ToNativeSearchSourceDto(streamEvent) };
                break;

            case "data-ninjachat.images":
                delta["images"] = (streamEvent.Event.Data as AIDataEventData)?.Data;
                break;

            case "data-ninjachat.follow-up-questions":
                delta["follow_up_questions"] = (streamEvent.Event.Data as AIDataEventData)?.Data;
                break;

            case "finish":
                var finishData = streamEvent.Event.Data as AIFinishEventData;
                finishReason = finishData?.FinishReason ?? "stop";

                if (finishData?.MessageMetadata?.Usage.ValueKind == JsonValueKind.Object)
                {
                    usage = JsonSerializer.Deserialize<object>(
                        finishData.MessageMetadata.Usage.GetRawText(),
                        JsonSerializerOptions.Web);
                }
                break;

            default:
                return null;
        }

        if (delta.Count == 0 && finishReason is null && usage is null)
            return null;

        var choice = new Dictionary<string, object?>
        {
            ["index"] = 0,
            ["delta"] = delta
        };

        if (!string.IsNullOrWhiteSpace(finishReason))
            choice["finish_reason"] = finishReason;

        return new ChatCompletionUpdate
        {
            Id = streamEvent.Event.Id ?? Guid.NewGuid().ToString("n"),
            Created = (streamEvent.Event.Timestamp ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds(),
            Model = (streamEvent.Event.Data as AIFinishEventData)?.Model ?? fallbackModel,
            Choices = [choice],
            Usage = usage
        };
    }

    private static string ExtractNativeSearchOutputText(AIOutput? output)
        => string.Concat(
            (output?.Items ?? [])
                .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
                .SelectMany(item => item.Content ?? [])
                .OfType<AITextContentPart>()
                .Select(part => part.Text));

    private static IEnumerable<object> ExtractNativeSearchSourceDtos(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            if (!string.Equals(item.Type, "source-url", StringComparison.OrdinalIgnoreCase)
                || item.Metadata is null)
            {
                continue;
            }

            if (!item.Metadata.TryGetValue("chatcompletions.source.url", out var urlValue)
                || string.IsNullOrWhiteSpace(urlValue?.ToString()))
            {
                continue;
            }

            yield return new Dictionary<string, object?>
            {
                ["url"] = urlValue?.ToString(),
                ["title"] = item.Metadata.TryGetValue("chatcompletions.source.title", out var titleValue) ? titleValue?.ToString() : null,
                ["content"] = item.Metadata.TryGetValue("ninjachat.source.content", out var contentValue) ? contentValue?.ToString() : null,
                ["published_date"] = item.Metadata.TryGetValue("ninjachat.source.published_date", out var publishedDateValue) ? publishedDateValue?.ToString() : null
            };
        }
    }

    private static object ToNativeSearchSourceDto(AIStreamEvent streamEvent)
    {
        var source = streamEvent.Event.Data as AISourceUrlEventData;
        var providerMetadata = source?.ProviderMetadata is not null
            && source.ProviderMetadata.TryGetValue(nameof(NinjaChat).ToLowerInvariant(), out var scoped)
                ? scoped
                : null;

        return new Dictionary<string, object?>
        {
            ["url"] = source?.Url,
            ["title"] = source?.Title,
            ["content"] = providerMetadata is not null && providerMetadata.TryGetValue("content", out var content) ? content?.ToString() : null,
            ["published_date"] = providerMetadata is not null && providerMetadata.TryGetValue("published_date", out var publishedDate) ? publishedDate?.ToString() : null
        };
    }

    private sealed class NinjaChatSearchChatCompletionStreamingState
    {
        public bool RoleEmitted { get; set; }
    }
}
