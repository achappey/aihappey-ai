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
    private static TextUIPart CreateTextPart(string text, string phase)
        => new()
        {
            Text = text,
            ProviderMetadata = CreatePhaseMetadata("openai", phase)
                .ToDictionary(entry => entry.Key, entry => entry.Value!)
        };

    private static AIInputItem CreateUnifiedTextMessage(
        string role,
        string text,
        Dictionary<string, object?>? textMetadata)
        => new()
        {
            Type = "message",
            Role = role,
            Content =
            [
                new AITextContentPart
                {
                    Text = text,
                    Type = "text",
                    Metadata = textMetadata
                }
            ]
        };

    private static Dictionary<string, object?> CreatePhaseMetadata(string providerId, string phase)
        => new()
        {
            [providerId] = new Dictionary<string, object>
            {
                ["phase"] = phase
            }
        };

    private static string AssertPhase(Dictionary<string, object?>? metadata, string providerId)
    {
        var scoped = Assert.IsType<Dictionary<string, object>>(Assert.Contains(providerId, metadata ?? []));
        return Assert.IsType<string>(scoped["phase"]);
    }

    [Fact]
    public void Responses_json_schema_roundtrips_through_unified_chat_completions_format()
    {
        var request = new ResponseRequest
        {
            Model = "openai/test-model",
            Text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "weather_output",
                    description = "A weather result",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        properties = new { city = new { type = "string" } },
                        required = new[] { "city" },
                        additionalProperties = false
                    }
                },
                verbosity = "low"
            }
        };

        var unified = request.ToUnifiedRequest("openai");
        var responseFormat = JsonSerializer.SerializeToElement(unified.ResponseFormat, JsonSerializerOptions.Web);
        var jsonSchema = responseFormat.GetProperty("json_schema");

        Assert.Equal("json_schema", responseFormat.GetProperty("type").GetString());
        Assert.Equal("weather_output", jsonSchema.GetProperty("name").GetString());
        Assert.Equal("A weather result", jsonSchema.GetProperty("description").GetString());
        Assert.True(jsonSchema.GetProperty("strict").GetBoolean());
        Assert.Equal("object", jsonSchema.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("low", unified.Verbosity);

        var roundtrip = unified.ToResponseRequest("openai");
        var text = JsonSerializer.SerializeToElement(roundtrip.Text, JsonSerializerOptions.Web);
        var format = text.GetProperty("format");

        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.Equal("weather_output", format.GetProperty("name").GetString());
        Assert.Equal("A weather result", format.GetProperty("description").GetString());
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal("object", format.GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("low", text.GetProperty("verbosity").GetString());
    }

    [Fact]
    public void Responses_text_without_structured_format_does_not_create_unified_response_format()
    {
        var request = new ResponseRequest
        {
            Model = "openai/test-model",
            Text = new { format = new { type = "text" } }
        };

        Assert.Null(request.ToUnifiedRequest("openai").ResponseFormat);
    }

    [Theory]
    [InlineData("download_tool")]
    [InlineData("upload_tool")]
    public void ToResponseRequest_suppresses_file_transfer_tool_before_misleading_native_replay_metadata_is_considered(string markerName)
    {
        var metadata = CreateToolSearchProviderMetadata(
            type: "tool_search_call",
            id: "tsc_misleading_download",
            execution: "server",
            callId: null,
            status: "completed");
        ((Dictionary<string, object?>)((Dictionary<string, object?>)metadata["messages.provider.call.metadata"]!)["openai"]!)[markerName] = true;

        var request = CreateToolSearchRequest(
            metadata,
            CreateClientToolSearchOutput("download_file"),
            providerExecuted: true);
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(
            request.ToResponseRequest("openai").Input?.Items);

        Assert.Empty(inputItems);
    }

    private const string XaiReasoningFollowUpFixturePath = "Fixtures/api-chat/raw/reasoning-with-signature-follow-up-chatrequest.json";
    private const string OpenAiCompactionFixturePath = "Fixtures/api-chat/raw/openai-with-compaction-chatrequest.json";

    [Fact]
    public void Vercel_chat_request_replays_only_the_latest_current_provider_compaction_and_subsequent_messages()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(OpenAiCompactionFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{OpenAiCompactionFixturePath}](Core/AIHappey.Tests/{OpenAiCompactionFixturePath}).");

        var responseRequest = chatRequest.ToUnifiedRequest("openai").ToResponseRequest("openai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);

        var compaction = Assert.IsType<ResponseCompactionItem>(inputItems[0]);
        Assert.Equal("cmp_080189eac4c3136b016a5f17a29138819db0d465937f1a249c", compaction.Id);
        Assert.False(string.IsNullOrWhiteSpace(compaction.EncryptedContent));

        var finalUserMessage = Assert.IsType<ResponseInputMessage>(inputItems[1]);
        Assert.Equal(ResponseRole.User, finalUserMessage.Role);
        Assert.Equal("preices dat", Assert.IsType<InputTextPart>(Assert.Single(finalUserMessage.Content.Parts!)).Text);

        Assert.Equal(2, inputItems.Count);
        Assert.Single(inputItems.OfType<ResponseCompactionItem>());
        Assert.DoesNotContain(inputItems.OfType<ResponseInputMessage>(), message =>
            message.Content.IsText && message.Content.Text == "bro");
    }

    [Fact]
    public void Vercel_chat_request_with_xai_encrypted_reasoning_follow_up_maps_to_responses_request_with_only_encrypted_reasoning_items()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(XaiReasoningFollowUpFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{XaiReasoningFollowUpFixturePath}](Core/AIHappey.Tests/{XaiReasoningFollowUpFixturePath}).");

        var expectedEncryptedContents = LoadEncryptedContents(json);
        var originalReasoningPartCount = CountReasoningParts(json);
        var responseRequest = chatRequest.ToUnifiedRequest("spacexai").ToResponseRequest("spacexai");

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
            Model = "spacexai/test-model",
            ProviderId = "spacexai",
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

        var responseRequest = request.ToResponseRequest("spacexai");
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
            Model = "spacexai/test-model",
            ProviderId = "spacexai",
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

        var responseRequest = request.ToResponseRequest("spacexai");
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
            Model = "spacexai/test-model",
            ProviderId = "spacexai",
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
                                    ["spacexai"] = new Dictionary<string, object>
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

        var responseRequest = request.ToResponseRequest("spacexai");
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
            Model = "spacexai/test-model",
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
                                ["spacexai"] = new Dictionary<string, object>
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

        var responseRequest = request.ToUnifiedRequest("spacexai").ToResponseRequest("spacexai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var reasoningItem = Assert.IsType<ResponseReasoningItem>(Assert.Single(inputItems.OfType<ResponseReasoningItem>()));

        Assert.Equal("rs_provider_reasoning_42", reasoningItem.Id);
        Assert.NotEqual("vercel-message-id", reasoningItem.Id);
        Assert.Equal("opaque-reasoning-state", reasoningItem.EncryptedContent);
    }

    [Fact]
    public void Client_tool_search_output_from_call_only_metadata_omits_id_and_preserves_call_id()
    {
        var request = CreateClientToolSearchRequest(
            resultMetadata: null,
            output: CreateClientToolSearchOutput("lookup_order"));

        var responseRequest = request.ToResponseRequest("openai");
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var call = Assert.Single(inputItems.OfType<ResponseToolSearchCallItem>());
        var output = Assert.Single(inputItems.OfType<ResponseToolSearchOutputItem>());

        Assert.Equal("tsc_call_item_123", call.Id);
        Assert.Equal("call_tool_search_123", call.CallId);
        Assert.Null(output.Id);
        Assert.Equal("client", output.Execution);
        Assert.Equal("call_tool_search_123", output.CallId);
        Assert.Equal("completed", output.Status);
        Assert.Single(output.Tools);

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(output, JsonSerializerOptions.Web));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("id", out _));
        Assert.Equal("tool_search_output", root.GetProperty("type").GetString());
        Assert.Equal("client", root.GetProperty("execution").GetString());
        Assert.Equal("call_tool_search_123", root.GetProperty("call_id").GetString());
        Assert.Equal("lookup_order", root.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.Equal("object", root.GetProperty("tools")[0].GetProperty("parameters").GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("tso_output_item_123", "tso_output_item_123")]
    [InlineData("tsc_call_item_456", null)]
    [InlineData("output_item_without_native_prefix", null)]
    public void Client_tool_search_output_retains_only_genuine_result_metadata_output_ids(
        string resultId,
        string? expectedId)
    {
        var request = CreateClientToolSearchRequest(
            resultMetadata: CreateToolSearchProviderMetadata(
                type: "tool_search_output",
                id: resultId,
                execution: "client",
                callId: "call_tool_search_123",
                status: "completed"),
            output: CreateClientToolSearchOutput("lookup_order"));

        var output = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(
                    request.ToResponseRequest("openai").Input?.Items)
                .OfType<ResponseToolSearchOutputItem>());

        Assert.Equal(expectedId, output.Id);
        Assert.Equal("call_tool_search_123", output.CallId);
    }

    [Fact]
    public void Client_tool_search_output_ignores_other_provider_result_metadata()
    {
        var otherProviderResultMetadata = new Dictionary<string, object?>
        {
            ["messages.provider.result.metadata"] = new Dictionary<string, object?>
            {
                ["anthropic"] = new Dictionary<string, object?>
                {
                    ["type"] = "tool_search_output",
                    ["id"] = "tso_anthropic_output",
                    ["execution"] = "client",
                    ["call_id"] = "call_anthropic"
                }
            }
        };
        var request = CreateClientToolSearchRequest(
            resultMetadata: otherProviderResultMetadata,
            output: CreateClientToolSearchOutput("lookup_order"));

        var output = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(
                    request.ToResponseRequest("openai").Input?.Items)
                .OfType<ResponseToolSearchOutputItem>());

        Assert.Null(output.Id);
        Assert.Equal("call_tool_search_123", output.CallId);
    }

    [Fact]
    public void Client_tool_search_output_allows_an_empty_loaded_tool_set()
    {
        var request = CreateClientToolSearchRequest(
            resultMetadata: null,
            output: new
            {
                structuredContent = new
                {
                    selectedTools = Array.Empty<object>()
                }
            });

        var output = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(
                    request.ToResponseRequest("openai").Input?.Items)
                .OfType<ResponseToolSearchOutputItem>());

        Assert.Null(output.Id);
        Assert.Empty(output.Tools);
        Assert.Equal("call_tool_search_123", output.CallId);
    }

    [Fact]
    public void Hosted_tool_search_output_preserves_its_native_tso_id()
    {
        var callMetadata = CreateToolSearchProviderMetadata(
            type: "tool_search_call",
            id: "tsc_hosted_call",
            execution: "server",
            callId: null,
            status: "completed");
        var resultMetadata = CreateToolSearchProviderMetadata(
            type: "tool_search_output",
            id: "tso_hosted_output",
            execution: "server",
            callId: null,
            status: "completed");
        foreach (var entry in resultMetadata)
            callMetadata[entry.Key] = entry.Value;

        var request = CreateToolSearchRequest(
            metadata: callMetadata,
            output: CreateClientToolSearchOutput("lookup_order"),
            providerExecuted: true);
        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(
            request.ToResponseRequest("openai").Input?.Items);

        Assert.Equal("tsc_hosted_call", Assert.Single(inputItems.OfType<ResponseToolSearchCallItem>()).Id);
        var output = Assert.Single(inputItems.OfType<ResponseToolSearchOutputItem>());
        Assert.Equal("tso_hosted_output", output.Id);
        Assert.Equal("server", output.Execution);
        Assert.Null(output.CallId);
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
                && providerMetadata.TryGetProperty("spacexai", out var xaiMetadata)
                && xaiMetadata.TryGetProperty("encrypted_content", out _))
            .Select(part => part.GetProperty("providerMetadata").GetProperty("spacexai").GetProperty("encrypted_content").GetString())
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

    private static AIRequest CreateClientToolSearchRequest(
        Dictionary<string, object?>? resultMetadata,
        object output)
    {
        var metadata = CreateToolSearchProviderMetadata(
            type: "tool_search_call",
            id: "tsc_call_item_123",
            execution: "client",
            callId: "call_tool_search_123",
            status: "completed");
        if (resultMetadata is not null)
        {
            foreach (var entry in resultMetadata)
                metadata[entry.Key] = entry.Value;
        }

        return CreateToolSearchRequest(metadata, output, providerExecuted: false);
    }

    private static AIRequest CreateToolSearchRequest(
        Dictionary<string, object?> metadata,
        object output,
        bool providerExecuted)
        => new()
        {
            Model = "openai/test-model",
            ProviderId = "openai",
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
                            new AIToolCallContentPart
                            {
                                Type = "tool-search-call",
                                ToolCallId = "call_tool_search_123",
                                ToolName = "tool_search",
                                Input = new { goal = "find an order lookup tool" },
                                Output = output,
                                State = "output-available",
                                ProviderExecuted = providerExecuted,
                                Metadata = metadata
                            }
                        ],
                        Metadata = metadata
                    }
                ]
            }
        };

    private static Dictionary<string, object?> CreateToolSearchProviderMetadata(
        string type,
        string? id,
        string execution,
        string? callId,
        string status)
    {
        var providerMetadata = new Dictionary<string, object?>
        {
            ["type"] = type,
            ["execution"] = execution,
            ["status"] = status
        };
        if (id is not null)
            providerMetadata["id"] = id;
        if (callId is not null)
            providerMetadata["call_id"] = callId;

        var channel = string.Equals(type, "tool_search_output", StringComparison.Ordinal)
            ? "messages.provider.result.metadata"
            : "messages.provider.call.metadata";
        return new Dictionary<string, object?>
        {
            [channel] = new Dictionary<string, object?>
            {
                ["openai"] = providerMetadata
            }
        };
    }

    private static object CreateClientToolSearchOutput(string toolName)
        => new
        {
            content = new[]
            {
                new
                {
                    type = "text",
                    text = $"Selected 1 tool: {toolName}"
                }
            },
            structuredContent = new
            {
                selectedTools = new[]
                {
                    new
                    {
                        name = toolName,
                        description = "Looks up an order.",
                        inputSchema = new
                        {
                            type = "object",
                            properties = new
                            {
                                orderId = new { type = "string" }
                            },
                            required = new[] { "orderId" }
                        }
                    }
                }
            }
        };

}
