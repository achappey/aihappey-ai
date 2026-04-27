using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesUnifiedMapperRequestTests
{
    private const string ProviderToolsWithFollowUpFixturePath = "Fixtures/api-chat/raw/provider-tools-with-follow-up-chatrequest.json";
    private const string AnthropicSkillsToolsFixturePath = "Fixtures/api-chat/raw/anthropic-with-skills-tools-chatrequest.json";

    [Fact]
    public void ToMessagesRequest_preserves_role_boundaries_for_plain_text_history()
    {
        var request = new AIRequest
        {
            Model = "anthropic/test-model",
            ProviderId = "anthropic",
            Input = new AIInput
            {
                Items =
                [
                    CreateTextInputItem("user", "First user message"),
                    CreateTextInputItem("assistant", "Assistant reply"),
                    CreateTextInputItem("user", "Follow-up question")
                ]
            }
        };

        var messagesRequest = request.ToMessagesRequest("anthropic");

        Assert.Collection(
            messagesRequest.Messages,
            message => AssertTextMessage(message, "user", "First user message"),
            message => AssertTextMessage(message, "assistant", "Assistant reply"),
            message => AssertTextMessage(message, "user", "Follow-up question"));
    }

    [Fact]
    public void Responses_reasoning_input_items_map_to_assistant_thinking_messages()
    {
        var responseRequest = new ResponseRequest
        {
            Model = "anthropic/test-model",
            Input = new ResponseInput([
                new ResponseInputMessage
                {
                    Role = ResponseRole.User,
                    Content = new ResponseMessageContent("Continue from this reasoning state.")
                },
                new ResponseReasoningItem
                {
                    Id = "rs_response_reasoning_123",
                    EncryptedContent = "opaque-anthropic-thinking-signature",
                    Summary =
                    [
                        new ResponseReasoningSummaryTextPart
                        {
                            Text = "I should use the preserved reasoning state."
                        }
                    ]
                }
            ])
        };

        var messagesRequest = responseRequest
            .ToUnifiedRequest("anthropic")
            .ToMessagesRequest("anthropic");

        Assert.Collection(
            messagesRequest.Messages,
            message => AssertTextMessage(message, "user", "Continue from this reasoning state."),
            message =>
            {
                Assert.Equal("assistant", message.Role);

                var thinkingBlock = Assert.Single(message.Content.Blocks!);
                Assert.Equal("thinking", thinkingBlock.Type);
                Assert.Equal("I should use the preserved reasoning state.", thinkingBlock.Thinking);
                Assert.Equal("opaque-anthropic-thinking-signature", thinkingBlock.Signature);
            });
    }

    [Fact]
    public void Vercel_chat_request_with_provider_executed_tool_and_follow_up_maps_to_ordered_messages_request()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(ProviderToolsWithFollowUpFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{ProviderToolsWithFollowUpFixturePath}](Core/AIHappey.Tests/{ProviderToolsWithFollowUpFixturePath}).");

        var messagesRequest = chatRequest.ToUnifiedRequest("anthropic").ToMessagesRequest("anthropic");

        Assert.NotNull(messagesRequest.System);
        Assert.Contains("chatBotInstructions", messagesRequest.System!.Text, StringComparison.Ordinal);
        Assert.Equal(3, messagesRequest.Messages.Count);

        Assert.Collection(
            messagesRequest.Messages,
            message => AssertTextMessage(message, "user", "search latest news about war in iran"),
            message =>
            {
                Assert.Equal("assistant", message.Role);

                var blocks = Assert.IsType<List<MessageContentBlock>>(message.Content.Blocks);

                var thinkingBlock = Assert.Single(blocks, block => block.Type == "thinking");
                Assert.Contains("I'll search for recent news about war in Iran.", thinkingBlock.Thinking, StringComparison.Ordinal);
                Assert.NotNull(thinkingBlock.Signature);

                var toolUseBlock = Assert.Single(blocks, block => block.Type == "server_tool_use");
                Assert.Equal("srvtoolu_01EJR47SppRDwQBqGRLQ2Gbm", toolUseBlock.Id);
                Assert.Null(toolUseBlock.Title);

                var toolResultBlock = Assert.Single(blocks, block => block.Type == "web_search_tool_result");
                Assert.Equal("srvtoolu_01EJR47SppRDwQBqGRLQ2Gbm", toolResultBlock.ToolUseId);

                var toolResultBlocks = Assert.IsType<IReadOnlyList<MessageContentBlock>>(toolResultBlock.Content?.Blocks, exactMatch: false);
                Assert.True(toolResultBlocks.Count >= 4);
                Assert.Equal("web_search_result", toolResultBlocks[0].Type);
                Assert.Equal(
                    "https://www.aljazeera.com/news/liveblog/2026/4/16/iran-war-live-pakistan-in-push-for-new-round-of-us-iran-peace-negotiations",
                    toolResultBlocks[3].Url);

                Assert.Contains(blocks, block => block.Type == "text"
                    && !string.IsNullOrWhiteSpace(block.Text)
                    && block.Text.StartsWith("## Laatste Nieuws over de Oorlog in Iran", StringComparison.Ordinal));
            },
            message => AssertTextMessage(message, "user", "Explain in max 10 words"));
    }

    [Fact]
    public void Vercel_chat_request_with_skills_tools_follow_up_preserves_typed_provider_tool_results()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(AnthropicSkillsToolsFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{AnthropicSkillsToolsFixturePath}](Core/AIHappey.Tests/{AnthropicSkillsToolsFixturePath}).");

        var messagesRequest = chatRequest.ToUnifiedRequest("anthropic").ToMessagesRequest("anthropic");
        var providerBlocks = messagesRequest.Messages
            .SelectMany(message => message.Content.Blocks ?? [])
            .Where(block => block.Type is "text_editor_code_execution_tool_result" or "bash_code_execution_tool_result")
            .ToList();

        Assert.Contains(messagesRequest.Messages, message =>
            message.Role == "assistant"
            && (message.Content.Blocks ?? []).Any(block => block.Type == "server_tool_use" && block.Id == "srvtoolu_01RJpTrakd4aKmzbabPbFUwL")
            && (message.Content.Blocks ?? []).Any(block => block.Type == "text_editor_code_execution_tool_result" && block.ToolUseId == "srvtoolu_01RJpTrakd4aKmzbabPbFUwL"));
        Assert.Contains(messagesRequest.Messages, message =>
            message.Role == "assistant"
            && (message.Content.Blocks ?? []).Any(block => block.Type == "server_tool_use" && block.Id == "srvtoolu_01Qi454Kc4mTAr1s1nYyLmh7")
            && (message.Content.Blocks ?? []).Any(block => block.Type == "bash_code_execution_tool_result" && block.ToolUseId == "srvtoolu_01Qi454Kc4mTAr1s1nYyLmh7"));
        Assert.DoesNotContain(messagesRequest.Messages, message =>
            message.Role == "user"
            && (message.Content.Blocks ?? []).Any(block => block.Type is "text_editor_code_execution_tool_result" or "bash_code_execution_tool_result" or "server_tool_use"));

        Assert.NotEmpty(providerBlocks);

        var textEditorErrorBlock = Assert.Single(providerBlocks, block => block.ToolUseId == "srvtoolu_01RJpTrakd4aKmzbabPbFUwL");
        Assert.Null(textEditorErrorBlock.IsError);
        Assert.True(textEditorErrorBlock.Content?.IsRaw);
        Assert.Equal("text_editor_code_execution_tool_result_error", textEditorErrorBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal("unavailable", textEditorErrorBlock.Content?.Raw?.GetProperty("error_code").GetString());

        var textEditorViewBlock = Assert.Single(providerBlocks, block => block.ToolUseId == "srvtoolu_01JiGoGeTqFGQHQXnNthCTX7");
        Assert.Null(textEditorViewBlock.IsError);
        Assert.True(textEditorViewBlock.Content?.IsRaw);
        Assert.Equal("text_editor_code_execution_view_result", textEditorViewBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal("text", textEditorViewBlock.Content?.Raw?.GetProperty("file_type").GetString());
        Assert.Equal(292, textEditorViewBlock.Content?.Raw?.GetProperty("num_lines").GetInt32());

        var bashResultBlock = Assert.Single(providerBlocks, block => block.ToolUseId == "srvtoolu_01Qi454Kc4mTAr1s1nYyLmh7");
        Assert.Null(bashResultBlock.IsError);
        Assert.True(bashResultBlock.Content?.IsRaw);
        Assert.Equal("bash_code_execution_result", bashResultBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal(0, bashResultBlock.Content?.Raw?.GetProperty("return_code").GetInt32());

        var bashOutputs = bashResultBlock.Content?.Raw?.GetProperty("content");
        Assert.True(bashOutputs?.ValueKind == JsonValueKind.Array);
        Assert.Equal("bash_code_execution_output", bashOutputs?.EnumerateArray().Single().GetProperty("type").GetString());
    }

    [Fact]
    public void ToMessagesRequest_infers_missing_inner_types_for_provider_executed_typed_results()
    {
        var request = new AIRequest
        {
            Model = "anthropic/test-model",
            ProviderId = "anthropic",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-bash_code_execution",
                                ToolCallId = "srvtoolu_missing_bash",
                                ToolName = "bash_code_execution",
                                Title = "bash_code_execution",
                                State = "output-available",
                                ProviderExecuted = true,
                                Output = JsonSerializer.SerializeToElement(new
                                {
                                    stdout = string.Empty,
                                    stderr = string.Empty,
                                    return_code = 0,
                                    content = new[] { new { file_id = "file_123" } }
                                }),
                                Metadata = CreateProviderExecutedMetadata(
                                    "anthropic",
                                    "bash_code_execution_tool_result",
                                    "srvtoolu_missing_bash")
                            },
                            new AIToolCallContentPart
                            {
                                Type = "tool-text_editor_code_execution",
                                ToolCallId = "srvtoolu_missing_text_editor_error",
                                ToolName = "text_editor_code_execution",
                                Title = "text_editor_code_execution",
                                State = "output-error",
                                ProviderExecuted = true,
                                Output = JsonSerializer.SerializeToElement(new
                                {
                                    error_code = "unavailable",
                                    error_message = "Parsing failed"
                                }),
                                Metadata = CreateProviderExecutedMetadata(
                                    "anthropic",
                                    "text_editor_code_execution_tool_result",
                                    "srvtoolu_missing_text_editor_error")
                            }
                        ]
                    }
                ]
            }
        };

        var messagesRequest = request.ToMessagesRequest("anthropic");
        var blocks = messagesRequest.Messages.SelectMany(message => message.Content.Blocks ?? []).ToList();

        var bashBlock = Assert.Single(blocks, block => block.ToolUseId == "srvtoolu_missing_bash");
        Assert.Null(bashBlock.IsError);
        Assert.True(bashBlock.Content?.IsRaw);
        Assert.Equal("bash_code_execution_result", bashBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal("bash_code_execution_output", bashBlock.Content?.Raw?.GetProperty("content").EnumerateArray().Single().GetProperty("type").GetString());

        var textEditorErrorBlock = Assert.Single(blocks, block => block.ToolUseId == "srvtoolu_missing_text_editor_error");
        Assert.Null(textEditorErrorBlock.IsError);
        Assert.True(textEditorErrorBlock.Content?.IsRaw);
        Assert.Equal("text_editor_code_execution_tool_result_error", textEditorErrorBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal("unavailable", textEditorErrorBlock.Content?.Raw?.GetProperty("error_code").GetString());
    }

    [Fact]
    public void ToMessagesRequest_prefers_structured_content_payload_when_provider_ui_wrapper_contains_typed_result()
    {
        var wrappedProviderOutput = JsonSerializer.SerializeToElement(new
        {
            structuredContent = new
            {
                type = "bash_code_execution_result",
                stdout = "done",
                stderr = string.Empty,
                return_code = 0,
                content = new[]
                {
                    new
                    {
                        type = "bash_code_execution_output",
                        file_id = "file_wrapped"
                    }
                }
            },
            content = Array.Empty<object>()
        });

        var request = new AIRequest
        {
            Model = "anthropic/test-model",
            ProviderId = "anthropic",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-bash_code_execution",
                                ToolCallId = "srvtoolu_wrapped_bash",
                                ToolName = "bash_code_execution",
                                Title = "bash_code_execution",
                                State = "output-available",
                                ProviderExecuted = true,
                                Output = wrappedProviderOutput,
                                Metadata = CreateProviderExecutedMetadata(
                                    "anthropic",
                                    "bash_code_execution_tool_result",
                                    "srvtoolu_wrapped_bash")
                            }
                        ]
                    }
                ]
            }
        };

        var messagesRequest = request.ToMessagesRequest("anthropic");
        var bashBlock = Assert.Single(
            messagesRequest.Messages.SelectMany(message => message.Content.Blocks ?? []),
            block => block.Type == "bash_code_execution_tool_result");

        Assert.Equal("bash_code_execution_tool_result", bashBlock.Type);
        Assert.Null(bashBlock.IsError);
        Assert.True(bashBlock.Content?.IsRaw);
        Assert.Equal("bash_code_execution_result", bashBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal("done", bashBlock.Content?.Raw?.GetProperty("stdout").GetString());
        Assert.Equal(0, bashBlock.Content?.Raw?.GetProperty("return_code").GetInt32());
    }

    [Fact]
    public void ToMessagesRequest_synthesizes_empty_success_payload_for_degraded_provider_executed_bash_output()
    {
        var request = new AIRequest
        {
            Model = "anthropic/test-model",
            ProviderId = "anthropic",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-bash_code_execution",
                                ToolCallId = "srvtoolu_degraded_bash",
                                ToolName = "bash_code_execution",
                                Title = "bash_code_execution",
                                State = "output-available",
                                ProviderExecuted = true,
                                Output = JsonSerializer.SerializeToElement(new
                                {
                                    content = Array.Empty<object>(),
                                    structuredContent = (object?)null,
                                    isError = (object?)null,
                                    task = (object?)null,
                                    _meta = (object?)null
                                }),
                                Metadata = CreateProviderExecutedMetadata(
                                    "anthropic",
                                    "bash_code_execution_tool_result",
                                    "srvtoolu_degraded_bash")
                            }
                        ]
                    }
                ]
            }
        };

        var messagesRequest = request.ToMessagesRequest("anthropic");
        var bashBlock = Assert.Single(
            messagesRequest.Messages.SelectMany(message => message.Content.Blocks ?? []),
            block => block.Type == "bash_code_execution_tool_result");

        Assert.Equal("bash_code_execution_tool_result", bashBlock.Type);
        Assert.Null(bashBlock.IsError);
        Assert.True(bashBlock.Content?.IsRaw);
        Assert.Equal("bash_code_execution_result", bashBlock.Content?.Raw?.GetProperty("type").GetString());
        Assert.Equal(string.Empty, bashBlock.Content?.Raw?.GetProperty("stdout").GetString());
        Assert.Equal(string.Empty, bashBlock.Content?.Raw?.GetProperty("stderr").GetString());
        Assert.Equal(0, bashBlock.Content?.Raw?.GetProperty("return_code").GetInt32());
        Assert.Equal(0, bashBlock.Content?.Raw?.GetProperty("content").GetArrayLength());
    }

    [Fact]
    public void ToMessagesRequest_omits_title_on_server_tool_use_blocks_for_provider_executed_round_trips()
    {
        var request = new AIRequest
        {
            Model = "anthropic/test-model",
            ProviderId = "anthropic",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                Type = "tool-web_search",
                                ToolCallId = "srvtoolu_title_roundtrip",
                                ToolName = "web_search",
                                Title = "web_search",
                                State = "output-available",
                                ProviderExecuted = true,
                                Input = JsonSerializer.SerializeToElement(new { query = "iran" }),
                                Output = JsonSerializer.SerializeToElement(new
                                {
                                    content = new[]
                                    {
                                        new
                                        {
                                            type = "web_search_result",
                                            title = "Result 1",
                                            url = "https://example.com/result-1"
                                        }
                                    }
                                }),
                                Metadata = CreateProviderExecutedMetadata(
                                    "anthropic",
                                    "web_search_tool_result",
                                    "srvtoolu_title_roundtrip",
                                    title: "web_search")
                            }
                        ]
                    }
                ]
            }
        };

        var messagesRequest = request.ToMessagesRequest("anthropic");
        var assistantMessage = Assert.Single(messagesRequest.Messages, message => message.Role == "assistant");
        var serverToolUseBlock = Assert.Single(assistantMessage.Content.Blocks!, block => block.Type == "server_tool_use");

        Assert.Equal("srvtoolu_title_roundtrip", serverToolUseBlock.Id);
        Assert.Equal("web_search", serverToolUseBlock.Name);
        Assert.Null(serverToolUseBlock.Title);

        var serializedServerToolUseBlock = JsonSerializer.Serialize(serverToolUseBlock, JsonSerializerOptions.Web);
        Assert.DoesNotContain("\"title\":", serializedServerToolUseBlock, StringComparison.Ordinal);
    }

    private static Dictionary<string, object?> CreateProviderExecutedMetadata(
        string providerId,
        string blockType,
        string toolUseId,
        string? title = null)
    {
        var providerMetadata = new Dictionary<string, object>
        {
            ["type"] = blockType,
            ["tool_use_id"] = toolUseId
        };

        if (!string.IsNullOrWhiteSpace(title))
            providerMetadata["title"] = title;

        return new Dictionary<string, object?>
        {
            ["messages.provider.id"] = providerId,
            ["messages.block.type"] = blockType,
            ["messages.provider.metadata"] = new Dictionary<string, Dictionary<string, object>>
            {
                [providerId] = providerMetadata
            }
        };
    }

    private static AIInputItem CreateTextInputItem(string role, string text)
        => new()
        {
            Type = "message",
            Role = role,
            Content =
            [
                new AITextContentPart
                {
                    Type = "text",
                    Text = text
                }
            ]
        };

    private static void AssertTextMessage(MessageParam message, string expectedRole, string expectedText)
    {
        Assert.Equal(expectedRole, message.Role);
        Assert.Equal(expectedText, ExtractMessageText(message));
    }

    private static void AssertTextMessageStartsWith(MessageParam message, string expectedRole, string expectedPrefix)
    {
        Assert.Equal(expectedRole, message.Role);
        Assert.StartsWith(expectedPrefix, ExtractMessageText(message), StringComparison.Ordinal);
    }

    private static string ExtractMessageText(MessageParam message)
    {
        if (!string.IsNullOrWhiteSpace(message.Content.Text))
            return message.Content.Text!;

        var block = Assert.Single(message.Content.Blocks!);
        Assert.Equal("text", block.Type);
        return block.Text ?? string.Empty;
    }
}
