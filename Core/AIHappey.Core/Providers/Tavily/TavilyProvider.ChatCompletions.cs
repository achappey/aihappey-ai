using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private static ChatCompletion ToChatCompletion(AIResponse response, string fallbackModel)
        => ToChatCompletion(ToCompletedTask(response), fallbackModel);

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

    private static ChatCompletionUpdate? ToChatCompletionUpdate(
        AIStreamEvent streamEvent,
        string fallbackModel,
        TavilyChatCompletionStreamingState state)
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
                delta["sources"] = new List<object> { ToSourceDto(ToSource(streamEvent)) };
                break;

            case "tool-input-available":
                if (!state.RoleEmitted)
                {
                    state.RoleEmitted = true;
                    delta["role"] = "assistant";
                }

                delta["tool_calls"] = CreateToolCallDelta(streamEvent);
                break;

            case "tool-output-available":
                if (!state.RoleEmitted)
                {
                    state.RoleEmitted = true;
                    delta["role"] = "assistant";
                }

                delta["tool_calls"] = CreateToolResponseDelta(streamEvent);
                break;

            case "data-tavily.structured-output":
                if (!state.RoleEmitted)
                {
                    state.RoleEmitted = true;
                    delta["role"] = "assistant";
                }

                delta["content"] = (streamEvent.Event.Data as AIDataEventData)?.Data;
                break;

            case "finish":
                var finishData = streamEvent.Event.Data as AIFinishEventData;
                finishReason = finishData?.FinishReason ?? "stop";

                if (finishData?.MessageMetadata?.Usage.ValueKind == JsonValueKind.Object)
                {
                    usage = JsonSerializer.Deserialize<object>(finishData.MessageMetadata.Usage.GetRawText(), JsonSerializerOptions.Web);
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
            Model = fallbackModel,
            Choices = [choice],
            Usage = usage
        };
    }

    private static TavilySource ToSource(AIStreamEvent streamEvent)
    {
        var sourceData = streamEvent.Event.Data as AISourceUrlEventData;
        var favicon = sourceData?.ProviderMetadata is not null
            && sourceData.ProviderMetadata.TryGetValue(nameof(Tavily).ToLowerInvariant(), out var providerMetadata)
            && providerMetadata.TryGetValue("favicon", out var faviconValue)
                ? faviconValue?.ToString()
                : null;

        return new TavilySource
        {
            Url = sourceData?.Url ?? string.Empty,
            Title = sourceData?.Title,
            Favicon = favicon
        };
    }

    private static object CreateToolCallDelta(AIStreamEvent streamEvent)
    {
        var data = streamEvent.Event.Data as AIToolInputAvailableEventData;
        var input = JsonSerializer.SerializeToElement(data?.Input ?? new { }, JsonSerializerOptions.Web);

        return new
        {
            type = "tool_call",
            tool_call = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = data?.ToolName ?? "tool",
                    ["id"] = streamEvent.Event.Id,
                    ["arguments"] = input.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.String
                        ? argumentsEl.GetString()
                        : null,
                    ["queries"] = input.TryGetProperty("queries", out var queriesEl) && queriesEl.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<object>(queriesEl.GetRawText(), JsonSerializerOptions.Web)
                        : null,
                    ["parent_tool_call_id"] = input.TryGetProperty("parent_tool_call_id", out var parentToolCallIdEl) && parentToolCallIdEl.ValueKind == JsonValueKind.String
                        ? parentToolCallIdEl.GetString()
                        : null
                }
            }
        };
    }

    private static object CreateToolResponseDelta(AIStreamEvent streamEvent)
    {
        var data = streamEvent.Event.Data as AIToolOutputAvailableEventData;
        var output = JsonSerializer.SerializeToElement(data?.Output ?? new { }, JsonSerializerOptions.Web);

        return new
        {
            type = "tool_response",
            tool_response = new[]
            {
                new Dictionary<string, object?>
                {
                    ["name"] = data?.ToolName ?? "tool",
                    ["id"] = streamEvent.Event.Id,
                    ["arguments"] = output.TryGetProperty("arguments", out var argumentsEl) && argumentsEl.ValueKind == JsonValueKind.String
                        ? argumentsEl.GetString()
                        : null,
                    ["sources"] = output.TryGetProperty("sources", out var sourcesEl) && sourcesEl.ValueKind == JsonValueKind.Array
                        ? JsonSerializer.Deserialize<object>(sourcesEl.GetRawText(), JsonSerializerOptions.Web)
                        : null,
                    ["parent_tool_call_id"] = output.TryGetProperty("parent_tool_call_id", out var parentToolCallIdEl) && parentToolCallIdEl.ValueKind == JsonValueKind.String
                        ? parentToolCallIdEl.GetString()
                        : null
                }
            }
        };
    }

    private sealed class TavilyChatCompletionStreamingState
    {
        public bool RoleEmitted { get; set; }
    }
}
