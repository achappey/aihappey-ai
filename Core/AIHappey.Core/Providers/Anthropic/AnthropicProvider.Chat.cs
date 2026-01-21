using AIHappey.Core.AI;
using ANT = Anthropic.SDK;
using Anthropic.SDK.Messaging;
using AIHappey.Common.Model;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using System.Net.Mime;
using System.Text;
using AIHappey.Core.Providers.Anthropic.Extensions;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.Anthropic;

public partial class AnthropicProvider : IModelProvider
{
    private sealed class ToolCallState
    {
        public string Id { get; }
        public string? Name { get; set; }
        public bool ProviderExecuted { get; set; }
        public StringBuilder InputJson { get; } = new();

        public ToolCallState(string id, string? name = null)
        {
            Id = id;
            Name = name;
        }
    }


    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = chatRequest.Model!;
        using var responseClient = new ANT.AnthropicClient(
            GetKey(),
            client: _client
        );

        IEnumerable<Message> inputItems = chatRequest.Messages
            .ToMessages();

        var systemMessages = chatRequest.Messages.ToSystemMessages();
        var stream = responseClient.Messages.StreamClaudeMessageAsync(chatRequest
                .ToMessageParameters(inputItems, model, systemMessages), ctx: cancellationToken);


        string? currentStreamId = null;
        bool reasoningOpen = false;
        bool messageOpen = false;
        string? currentSignature = null;

        var toolCalls = new Dictionary<string, ToolCallState>();
        string? currentToolCallId = null; // laatst geopende tool waar JSON-delta’s in komen

        await foreach (var update in stream.WithCancellation(cancellationToken))
        {
            foreach (var content in update.ContentBlock?.Content ?? [])
            {
                if (content is BashCodeExecutionResultContent bashResult)
                {
                    foreach (var contentItem in bashResult.Content ?? [])
                    {
                        if (contentItem is BashCodeExecutionOutputContent result)
                        {
                            yield return await responseClient.GetFileUIPart(result.FileId, cancellationToken);
                        }

                    }

                }
            }

            switch (update.Type)
            {
                case "message_start":

                    currentStreamId = Guid.NewGuid().ToString();
                    reasoningOpen = false;
                    messageOpen = false;
                    currentSignature = null;
                    break;

                case "content_block_start":
                    switch (update.ContentBlock?.Type)
                    {
                        case "thinking":
                            if (messageOpen)
                            {
                                messageOpen = false;
                                yield return currentStreamId!.ToTextEndUIMessageStreamPart();
                            }

                            currentStreamId = Guid.NewGuid().ToString();
                            reasoningOpen = true;
                            yield return new ReasoningStartUIPart { Id = currentStreamId! };
                            break;
                        case "text":
                            if (!messageOpen)
                            {
                                messageOpen = true;
                                yield return currentStreamId!.ToTextStartUIMessageStreamPart();
                            }
                            break;
                        case "server_tool_use":
                        case "mcp_tool_use":
                        case "tool_use":
                            if (messageOpen)
                            {
                                messageOpen = false;
                                yield return currentStreamId!.ToTextEndUIMessageStreamPart();
                            }

                            var id = update.ContentBlock!.Id!;
                            var name = update.ContentBlock.Name!;

                            var state = new ToolCallState(id, name)
                            {
                                // server_tool_use / mcp_tool_use zijn provider-executed
                                ProviderExecuted = update.ContentBlock.Type is "server_tool_use" or "mcp_tool_use"
                            };

                            toolCalls[id] = state;
                            currentToolCallId = id;

                            // jouw bestaande start-events:
                            if (update.ContentBlock.Type is "server_tool_use" or "mcp_tool_use")
                            {
                                yield return ToolCallStreamingStartPart.CreateProviderExecuted(id, name);
                            }
                            else // "tool_use" (normale tool)
                            {
                                yield return new ToolCallStreamingStartPart
                                {
                                    ToolCallId = id,
                                    ToolName = name
                                };
                            }

                            break;


                        case "mcp_tool_result":
                            {
                                var block = update.ContentBlock;
                                if (block is null)
                                    break;

                                // Pak het échte Anthropic tool-id
                                var toolId = block.ToolUseId ?? currentToolCallId;
                                if (string.IsNullOrEmpty(toolId))
                                    break; // geen geldig id → geen tool-output event sturen

                                var item = new CallToolResult
                                {
                                    IsError = false,
                                    Content = [..block.Content
                                        .OfType<TextContent>()
                                        .Select(a => a.Text.ToTextContentBlock())]
                                };

                                // Aanname: helper is al omgebouwd naar Vercel v5 shape (tool-output-end)
                                yield return item.ToProviderToolOutputAvailablePart(toolId);

                                // verder niets resetten; state per tool-id
                                continue;
                            }
                        case "code_execution_tool_result":
                        case "bash_code_execution_tool_result":
                        case "text_editor_code_execution_tool_result":
                        case "web_search_tool_result":
                        case "web_fetch_tool_result":
                            {
                                var block = update.ContentBlock;
                                if (block is null)
                                    break;

                                var toolId = block.ToolUseId
                                             ?? currentToolCallId
                                             ?? toolCalls.Keys.LastOrDefault();

                                if (string.IsNullOrEmpty(toolId))
                                    break;

                                if (block.Type!.EndsWith("code_execution_tool_result"))
                                {
                                    var result = new CallToolResult
                                    {
                                        IsError = false,
                                        Content = [JsonSerializer.Serialize(block.Content)
                                        .ToTextContentBlock()]
                                    };

                                    yield return result.ToProviderToolOutputAvailablePart(toolId);
                                }
                                else if (block.Type == "web_search_tool_result")
                                {
                                    var searchResults = block.Content.OfType<WebSearchResultContent>();

                                    var result = new CallToolResult
                                    {
                                        IsError = false,
                                        Content = [..searchResults.Select(t => new EmbeddedResourceBlock
                                        {
                                            Resource = new TextResourceContents
                                            {
                                                Uri = t.Url,
                                                MimeType = MediaTypeNames.Application.Json,
                                                Text = JsonSerializer.Serialize(t, JsonSerializerOptions.Web)
                                            }
                                        })]
                                    };

                                    yield return result.ToProviderToolOutputAvailablePart(toolId);

                                    foreach (var searchResult in searchResults)
                                    {
                                        yield return searchResult.ToSourceUIPart();
                                    }
                                }
                                else if (block.Type == "web_fetch_tool_result")
                                {
                                    var result = new CallToolResult
                                    {
                                        IsError = false,
                                        Content = [.. block.Content.Select(a => JsonSerializer.Serialize(a).ToTextContentBlock())]
                                    };

                                    yield return result.ToProviderToolOutputAvailablePart(toolId);
                                }

                                // Geen globale tool-state meer resetten, alles zit nu in toolCalls
                                break;
                            }
                        default:

                            break;
                    }

                    break;

                case "content_block_delta":
                    if (update.Delta?.Type == "thinking_delta" && reasoningOpen)
                    {
                        yield return new ReasoningDeltaUIPart
                        {
                            Id = currentStreamId!,
                            Delta = update.Delta.Thinking
                        };
                    }
                    else if (update.Delta?.Type == "text_delta" && messageOpen)
                    {
                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = currentStreamId!,
                            Delta = update.Delta.Text
                        };
                    }
                    else if (update.Delta?.Type == "signature_delta")
                    {
                        currentSignature = update.Delta.Signature;
                    }
                    else if (update.Delta?.Type == "input_json_delta"
                            && !string.IsNullOrEmpty(update.Delta.PartialJson)
                            && currentToolCallId is not null
                            && toolCalls.TryGetValue(currentToolCallId, out var state))
                    {
                        state.InputJson.Append(update.Delta.PartialJson);

                        yield return new ToolCallDeltaPart
                        {
                            ToolCallId = state.Id,
                            InputTextDelta = update.Delta.PartialJson
                        };
                    }
                    break;

                case "content_block_stop":
                    if (reasoningOpen)
                    {
                        reasoningOpen = false;
                        yield return new ReasoningEndUIPart
                        {
                            Id = currentStreamId!,
                            ProviderMetadata = !string.IsNullOrEmpty(currentSignature)
                                ? new Dictionary<string, object> { { "signature", currentSignature } }
                                    .ToProviderMetadata()
                                : null
                        };

                        currentStreamId = Guid.NewGuid().ToString();
                    }


                    break;

                case "message_delta":
                    {
                        var stopReason = update.Delta?.StopReason;

                        // Normale finish (geen tool_use)
                        if (!string.IsNullOrEmpty(stopReason) && stopReason != "tool_use")
                        {
                            if (messageOpen)
                            {
                                messageOpen = false;
                                yield return new TextEndUIMessageStreamPart { Id = currentStreamId! };
                            }

                            var outputTokens = update.Usage?.OutputTokens ?? 0;
                            var inputTokens = update.Usage?.InputTokens ?? 0;
                            var cacheReadTokens = update.Usage?.CacheReadInputTokens ?? 0;

                            yield return stopReason
                                .ToFinishReason()
                                .ToFinishUIPart(
                                    model,
                                    outputTokens,
                                    inputTokens,
                                    outputTokens + inputTokens + cacheReadTokens,
                                    temperature: chatRequest.Temperature,
                                    cachedInputTokens: cacheReadTokens
                                );
                        }

                        // StopReason == tool_use  → alle open toolcalls uitsturen
                        if (stopReason == "tool_use")
                        {
                            if (messageOpen)
                            {
                                messageOpen = false;
                                yield return new TextEndUIMessageStreamPart { Id = currentStreamId! };
                            }

                            foreach (var state in toolCalls.Values)
                            {
                                var json = state.InputJson.ToString();
                                object inputObject;

                                if (string.IsNullOrWhiteSpace(json))
                                {
                                    // geen input binnengekomen → stuur lege payload
                                    inputObject = new object();
                                }
                                else
                                {
                                    inputObject = JsonSerializer.Deserialize<object>(json)!;
                                }

                                var providerExecuted = state.ProviderExecuted
                                                            || state.Id.StartsWith("srvtoolu_")
                                                            || state.Id.StartsWith("mcptoolu_");

                                var toolName = state.Name ?? state.Id;

                                yield return new ToolCallPart
                                {
                                    ToolCallId = state.Id,
                                    ToolName = toolName,
                                    Title = chatRequest.Tools?.FirstOrDefault(a => a.Name == toolName)?.Title,
                                    Input = inputObject,
                                    ProviderExecuted = providerExecuted
                                };

                                if (!providerExecuted)
                                {
                                    yield return new ToolApprovalRequestPart
                                    {
                                        ToolCallId = state.Id,
                                        ApprovalId = Guid.NewGuid().ToString(),
                                    };
                                }
                            }

                            toolCalls.Clear();
                            currentToolCallId = null;
                        }

                        break;
                    }

                case "message_stop":
                    currentStreamId = null;
                    currentSignature = null;

                    break;

            }

            if (update.ToolCalls.Count != 0 && messageOpen)
            {
                messageOpen = false;
                yield return currentStreamId!.ToTextEndUIMessageStreamPart();
            }

            var isToolResult =
                update.ContentBlock?.Type?.EndsWith("tool_result") == true
                || update.ContentBlock?.Type == "mcp_tool_result";

            if (!isToolResult)
            {
                foreach (var part in update.ToStreamingResponseUpdate(currentStreamId))
                    yield return part;
            }

        }
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}