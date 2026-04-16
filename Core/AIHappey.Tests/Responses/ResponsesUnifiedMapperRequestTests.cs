using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesUnifiedMapperRequestTests
{
    private const string XaiReasoningFollowUpFixturePath = "Fixtures/api-chat/raw/reasoning-with-signature-follow-up-chatrequest.json";

    [Fact]
    public void Vercel_chat_request_with_xai_encrypted_reasoning_follow_up_maps_to_responses_request_with_only_encrypted_reasoning_items()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(XaiReasoningFollowUpFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{XaiReasoningFollowUpFixturePath}](Core/AIHappey.Tests/{XaiReasoningFollowUpFixturePath}).");

        var expectedEncryptedContents = LoadEncryptedContents(json);
        var originalReasoningPartCount = CountReasoningParts(json);
        var responseRequest = chatRequest.ToUnifiedRequest("xai").ToResponseRequest("xai");

        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItems = inputItems.OfType<ResponseReasoningItem>().ToList();

        Assert.True(originalReasoningPartCount > expectedEncryptedContents.Count);
        Assert.Equal(expectedEncryptedContents, reasoningItems.Select(item => item.EncryptedContent!).ToList());
        Assert.All(reasoningItems, item => Assert.False(string.IsNullOrWhiteSpace(item.EncryptedContent)));
        Assert.All(reasoningItems, item => Assert.True(string.IsNullOrWhiteSpace(item.Id)));
        Assert.Equal("The user's message is \"yow\". That's informal, like a greeting. It could be \"yo\" or \"yow\" as in \"yo what's up\".\n", Assert.Single(reasoningItems[0].Summary).Text);
        Assert.Equal("The user asked: \"search latest new about war in iran\"\nI searched for the latest news on the war in Iran. As of April 16, 2026, the situation involves ongoing tensions between Iran and Israel, with recent escalations including missile strikes and retaliatory actions.", Assert.Single(reasoningItems[1].Summary).Text);
        Assert.Empty(reasoningItems[2].Summary);
        Assert.Empty(inputItems.OfType<ResponseFunctionCallItem>());
        Assert.Empty(inputItems.OfType<ResponseFunctionCallOutputItem>());

        Assert.Collection(
            inputItems,
            item => Assert.IsType<ResponseInputMessage>(item),
            item => Assert.IsType<ResponseInputMessage>(item),
            item => Assert.IsType<ResponseReasoningItem>(item),
            item => Assert.IsType<ResponseInputMessage>(item),
            item => Assert.IsType<ResponseInputMessage>(item),
            item => Assert.IsType<ResponseReasoningItem>(item),
            item => Assert.IsType<ResponseReasoningItem>(item),
            item => Assert.IsType<ResponseInputMessage>(item),
            item => Assert.IsType<ResponseInputMessage>(item));

        Assert.Equal(expectedEncryptedContents.Count, reasoningItems.Count);
    }

    [Fact]
    public void Plaintext_reasoning_remains_replayable_when_no_matching_encrypted_state_exists()
    {
        var request = new AIRequest
        {
            Model = "xai/test-model",
            ProviderId = "xai",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIReasoningContentPart
                            {
                                Text = "First think through the answer.",
                                Type = "reasoning"
                            },
                            new AITextContentPart
                            {
                                Text = "Done.",
                                Type = "text"
                            }
                        ]
                    }
                ]
            }
        };

        var responseRequest = request.ToResponseRequest("xai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItem = Assert.IsType<ResponseReasoningItem>(Assert.Single(inputItems.OfType<ResponseReasoningItem>()));

        Assert.Null(reasoningItem.EncryptedContent);
        Assert.Equal("First think through the answer.", Assert.Single(reasoningItem.Summary).Text);
    }

    [Fact]
    public void Encrypted_reasoning_from_another_provider_is_not_forwarded_to_current_provider()
    {
        var request = new AIRequest
        {
            Model = "xai/test-model",
            ProviderId = "xai",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIReasoningContentPart
                            {
                                Text = "Use plain reasoning for xAI replay.",
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["anthropic"] = new Dictionary<string, object?>
                                    {
                                        ["encrypted_content"] = "anthropic-secret"
                                    }
                                },
                                Type = "reasoning"
                            },
                            new AITextContentPart
                            {
                                Text = "Visible answer.",
                                Type = "text"
                            }
                        ]
                    }
                ]
            }
        };

        var responseRequest = request.ToResponseRequest("xai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItem = Assert.IsType<ResponseReasoningItem>(Assert.Single(inputItems.OfType<ResponseReasoningItem>()));

        Assert.Null(reasoningItem.EncryptedContent);
        Assert.Equal("Use plain reasoning for xAI replay.", Assert.Single(reasoningItem.Summary).Text);

        var serialized = JsonSerializer.Serialize(responseRequest, JsonSerializerOptions.Web);
        Assert.DoesNotContain("anthropic-secret", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Reasoning_item_id_is_reconstructed_from_provider_scoped_metadata_when_top_level_id_is_missing()
    {
        var request = new AIRequest
        {
            Model = "xai/test-model",
            ProviderId = "xai",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AIReasoningContentPart
                            {
                                Type = "reasoning",
                                Text = "Encrypted reasoning.",
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["xai"] = new Dictionary<string, object>
                                    {
                                        ["id"] = "resp_reasoning_item_123",
                                        ["item_id"] = "resp_reasoning_item_123",
                                        ["encrypted_content"] = "opaque-encrypted-content"
                                    }
                                }
                            }
                        ]
                    }
                ]
            }
        };

        var responseRequest = request.ToResponseRequest("xai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItem = Assert.IsType<ResponseReasoningItem>(Assert.Single(inputItems.OfType<ResponseReasoningItem>()));

        Assert.Equal("resp_reasoning_item_123", reasoningItem.Id);
        Assert.Equal("opaque-encrypted-content", reasoningItem.EncryptedContent);
    }

    [Fact]
    public void Vercel_reasoning_part_provider_item_id_is_preferred_over_message_id_during_responses_replay()
    {
        var request = new ChatRequest
        {
            Model = "xai/test-model",
            Messages =
            [
                new UIMessage
                {
                    Id = "vercel-message-id",
                    Role = Role.assistant,
                    Parts =
                    [
                        new ReasoningUIPart
                        {
                            Id = string.Empty,
                            Text = "Encrypted reasoning from saved UI part.",
                            ProviderMetadata = new Dictionary<string, object>
                            {
                                ["xai"] = new Dictionary<string, object>
                                {
                                    ["id"] = "rs_provider_reasoning_42",
                                    ["item_id"] = "rs_provider_reasoning_42",
                                    ["encrypted_content"] = "opaque-reasoning-state"
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var responseRequest = request.ToUnifiedRequest("xai").ToResponseRequest("xai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItem = Assert.IsType<ResponseReasoningItem>(Assert.Single(inputItems.OfType<ResponseReasoningItem>()));

        Assert.Equal("rs_provider_reasoning_42", reasoningItem.Id);
        Assert.NotEqual("vercel-message-id", reasoningItem.Id);
        Assert.Equal("opaque-reasoning-state", reasoningItem.EncryptedContent);
    }

    private static List<string> LoadEncryptedContents(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Where(message => message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .Where(part => part.TryGetProperty("providerMetadata", out var providerMetadata)
                && providerMetadata.TryGetProperty("xai", out var xaiMetadata)
                && xaiMetadata.TryGetProperty("encrypted_content", out _))
            .Select(part => part.GetProperty("providerMetadata").GetProperty("xai").GetProperty("encrypted_content").GetString())
            .Where(static encryptedContent => !string.IsNullOrWhiteSpace(encryptedContent))
            .Select(static encryptedContent => encryptedContent!)
            .ToList();
    }

    private static int CountReasoningParts(string json)
    {
        using var document = JsonDocument.Parse(json);

        return document.RootElement
            .GetProperty("messages")
            .EnumerateArray()
            .Where(message => message.TryGetProperty("role", out var role) && role.GetString() == "assistant")
            .SelectMany(message => message.GetProperty("parts").EnumerateArray())
            .Count(part => part.TryGetProperty("type", out var type) && type.GetString() == "reasoning");
    }

}
