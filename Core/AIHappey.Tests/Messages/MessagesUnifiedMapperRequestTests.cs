using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesUnifiedMapperRequestTests
{
    private const string ProviderToolsWithFollowUpFixturePath = "Fixtures/api-chat/raw/provider-tools-with-follow-up-chatrequest.json";

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
    public void Vercel_chat_request_with_provider_executed_tool_and_follow_up_maps_to_ordered_messages_request()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(ProviderToolsWithFollowUpFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{ProviderToolsWithFollowUpFixturePath}](Core/AIHappey.Tests/{ProviderToolsWithFollowUpFixturePath}).");

        var messagesRequest = chatRequest.ToUnifiedRequest("anthropic").ToMessagesRequest("anthropic");

        Assert.NotNull(messagesRequest.System);
        Assert.Contains("chatBotInstructions", messagesRequest.System!.Text, StringComparison.Ordinal);
        Assert.Equal(5, messagesRequest.Messages.Count);

        Assert.Collection(
            messagesRequest.Messages,
            message => AssertTextMessage(message, "user", "search latest news about war in iran"),
            message =>
            {
                Assert.Equal("assistant", message.Role);

                var block = Assert.Single(message.Content.Blocks!);
                Assert.Equal("thinking", block.Type);
                Assert.Contains("I'll search for recent news about war in Iran.", block.Thinking, StringComparison.Ordinal);
                Assert.NotNull(block.Signature);
            },
            message =>
            {
                Assert.Equal("user", message.Role);

                var block = Assert.Single(message.Content.Blocks!);
                Assert.Equal("web_search_tool_result", block.Type);
                Assert.Equal("srvtoolu_01EJR47SppRDwQBqGRLQ2Gbm", block.ToolUseId);

                var toolResultBlocks = Assert.IsAssignableFrom<IReadOnlyList<MessageContentBlock>>(block.Content?.Blocks);
                Assert.True(toolResultBlocks.Count >= 4);
                Assert.Equal("web_search_result", toolResultBlocks[0].Type);
                Assert.Equal(
                    "https://www.aljazeera.com/news/liveblog/2026/4/16/iran-war-live-pakistan-in-push-for-new-round-of-us-iran-peace-negotiations",
                    toolResultBlocks[3].Url);
            },
            message => AssertTextMessageStartsWith(message, "assistant", "## Laatste Nieuws over de Oorlog in Iran"),
            message => AssertTextMessage(message, "user", "Explain in max 10 words"));
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
