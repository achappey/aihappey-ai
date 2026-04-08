using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Kirha;

public partial class KirhaProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var query = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(query))
        {
            yield return "Kirha search requires text from the last user message.".ToErrorUIPart();
            yield return "error".ToFinishUIPart(chatRequest.Model, 0, 0, 0, chatRequest.Temperature);
            yield break;
        }

        var passthrough = GetRawProviderPassthroughFromChatRequest(chatRequest);
        var result = await ExecuteKirhaSearchAsync(chatRequest.Model, query, passthrough, cancellationToken);

        foreach (var reasoning in result.ReasoningItems)
        {
            var reasoningMetadata = reasoning.Metadata.ToDictionary(k => k.Key, v => (object?)v.Value)
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);

            yield return new ReasoningStartUIPart
            {
                Id = reasoning.Id,
                ProviderMetadata = reasoningMetadata
            };

            yield return new ReasoningDeltaUIPart
            {
                Id = reasoning.Id,
                Delta = reasoning.Text,
                ProviderMetadata = reasoningMetadata
            };

            yield return new ReasoningEndUIPart
            {
                Id = reasoning.Id,
                ProviderMetadata = reasoningMetadata.ToProviderMetadata("kirha")
            };
        }

        foreach (var toolCall in result.ToolCalls)
        {
            var inputJson = System.Text.Json.JsonSerializer.Serialize(toolCall.Input, Json);

            yield return new ToolCallPart
            {
                ToolCallId = toolCall.Id,
                ToolName = toolCall.ToolName,
                Title = chatRequest.Tools?.FirstOrDefault(t => t.Name == toolCall.ToolName)?.Title,
                ProviderExecuted = true,
                Input = toolCall.Input
            };

            yield return new ToolOutputAvailablePart
            {
                ToolCallId = toolCall.Id,
                ProviderExecuted = true,
                Output = new ModelContextProtocol.Protocol.CallToolResult
                {
                    IsError = false,
                    StructuredContent = toolCall.Output != null
                        ? JsonSerializer.SerializeToElement(toolCall.Output, JsonSerializerOptions.Web) : null,
                    Content = toolCall.Output != null ? [] :
                    [
                        "Kirha executed the provider-side search step but no explicit raw output payload was returned."
                            .ToTextContentBlock()
                    ]
                }
            };
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            var streamId = result.Response.Id ?? Guid.NewGuid().ToString("n");
            yield return streamId.ToTextStartUIMessageStreamPart();
            yield return new TextDeltaUIMessageStreamPart { Id = streamId, Delta = result.Summary };
            yield return streamId.ToTextEndUIMessageStreamPart();
        }

        var usage = result.Response.Usage;
        var promptTokens = usage?.Estimated ?? 0;
        var completionTokens = usage?.Consumed ?? 0;
        yield return "stop".ToFinishUIPart(
            model: chatRequest.Model,
            outputTokens: completionTokens,
            inputTokens: promptTokens,
            totalTokens: promptTokens + completionTokens,
            temperature: chatRequest.Temperature,
            extraMetadata: result.Metadata.ToDictionary(k => k.Key, v => (object?)v.Value)
                .Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!));
    }
}
