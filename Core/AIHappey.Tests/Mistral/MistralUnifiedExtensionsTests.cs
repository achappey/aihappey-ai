using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Mistral;

public sealed class MistralUnifiedExtensionsTests
{
    [Fact]
    public void Unified_conversation_inputs_include_text_messages_and_client_tool_calls()
    {
        var request = new AIRequest
        {
            ProviderId = "mistral",
            Model = "mistral-large-latest",
            Input = new AIInput
            {
                Text = "Top level input",
                Items =
                [
                    new AIInputItem
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = "Assistant reply"
                            },
                            new AIToolCallContentPart
                            {
                                ToolCallId = "tool_123",
                                Type = "tool-call",
                                ToolName = "lookup_weather",
                                Input = new { city = "Amsterdam" },
                                Output = new { forecast = "sunny" },
                                ProviderExecuted = false
                            }
                        ]
                    }
                ]
            }
        };

        var inputs = request.BuildMistralUnifiedConversationInputs();

        Assert.Equal(4, inputs.Count);

        var serialized = JsonSerializer.Serialize(inputs, JsonSerializerOptions.Web);
        Assert.Contains("Top level input", serialized, StringComparison.Ordinal);
        Assert.Contains("Assistant reply", serialized, StringComparison.Ordinal);
        Assert.Contains("function.call", serialized, StringComparison.Ordinal);
        Assert.Contains("function.result", serialized, StringComparison.Ordinal);
        Assert.Contains("lookup_weather", serialized, StringComparison.Ordinal);
        Assert.Contains("Amsterdam", serialized, StringComparison.Ordinal);
        Assert.Contains("sunny", serialized, StringComparison.Ordinal);
    }

    [Fact]
    public void Unified_conversation_inputs_do_not_duplicate_function_call_for_split_responses_replay()
    {
        var request = new AIRequest
        {
            ProviderId = "mistral",
            Model = "mistral-large-latest",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "function_call",
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                ToolCallId = "call_123",
                                Type = "function_call",
                                ToolName = "lookup_weather",
                                Input = new { city = "Amsterdam" },
                                ProviderExecuted = false
                            }
                        ]
                    },
                    new AIInputItem
                    {
                        Type = "function_call_output",
                        Role = "tool",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                ToolCallId = "call_123",
                                Type = "function_call_output",
                                Output = new { forecast = "sunny" },
                                ProviderExecuted = false
                            }
                        ]
                    }
                ]
            }
        };

        var inputs = request.BuildMistralUnifiedConversationInputs();

        Assert.Equal(2, inputs.Count);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(inputs, JsonSerializerOptions.Web));
        var entries = json.RootElement.EnumerateArray().ToList();

        Assert.Collection(
            entries,
            entry =>
            {
                Assert.Equal("function.call", entry.GetProperty("type").GetString());
                Assert.Equal("call_123", entry.GetProperty("tool_call_id").GetString());
                Assert.Equal("lookup_weather", entry.GetProperty("name").GetString());
                Assert.Equal("{\"city\":\"Amsterdam\"}", entry.GetProperty("arguments").GetString());
            },
            entry =>
            {
                Assert.Equal("function.result", entry.GetProperty("type").GetString());
                Assert.Equal("call_123", entry.GetProperty("tool_call_id").GetString());
                Assert.Equal("{\"forecast\":\"sunny\"}", entry.GetProperty("result").GetString());
            });

        Assert.Single(entries, entry => entry.GetProperty("type").GetString() == "function.call");
        Assert.Single(entries, entry => entry.GetProperty("type").GetString() == "function.result");
    }

    [Fact]
    public void Provider_capture_request_is_resolved_from_mistral_metadata()
    {
        var request = new AIRequest
        {
            ProviderId = "mistral",
            Metadata = new Dictionary<string, object?>
            {
                ["mistral"] = new Dictionary<string, object?>
                {
                    ["capture"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["relativeDirectory"] = "mistral/tests",
                        ["fileName"] = "conversation-response"
                    }
                }
            }
        };

        var capture = request.GetMistralBackendCapture("mistral");

        Assert.NotNull(capture);
        Assert.True(capture!.Enabled);
        Assert.Equal("mistral/tests", capture.RelativeDirectory);
        Assert.Equal("conversation-response", capture.FileName);
    }

    [Fact]
    public void Conversation_stream_event_and_content_parts_are_parsed_into_testable_helpers()
    {
        var payload = """
            {
              "conversation_id": "conv_123",
              "content": [
                { "type": "output_text", "text": "Hello world" },
                { "type": "tool_reference", "url": "https://docs.example.com", "title": "Docs" },
                { "type": "tool_file", "file_id": "file_123", "file_name": "chart.png", "file_type": "image/png" }
              ]
            }
            """;

        var evt = MistralExtensions.ParseConversationStreamEventEnvelope("message.output.delta", payload);
        var parts = MistralExtensions.EnumerateConversationContentParts(evt.GetNode("content")).ToList();

        Assert.Equal("message.output.delta", evt.Type);
        Assert.Equal("conv_123", evt.GetString("conversation_id"));

        Assert.Collection(
            parts,
            part =>
            {
                Assert.Equal("output_text", part.Type);
                Assert.Equal("Hello world", part.Text);
            },
            part =>
            {
                Assert.Equal("tool_reference", part.Type);
                Assert.Equal("https://docs.example.com", part.Url);
                Assert.Equal("Docs", part.Title);
            },
            part =>
            {
                Assert.Equal("tool_file", part.Type);
                Assert.Equal("file_123", part.FileId);
                Assert.Equal("chart.png", part.FileName);
                Assert.Equal("image/png", part.FileType);
            });
    }

    [Fact]
    public void Raw_mistral_metadata_is_preferred_when_mapping_content_parts()
    {
        var raw = JsonNode.Parse("""{ "type": "document_url", "document_url": "https://example.com/raw", "document_name": "Raw doc" }""")
                  ?? throw new InvalidOperationException("Expected raw node.");

        var part = new AITextContentPart
        {
            Text = "ignored because raw metadata wins",
            Type = "text",
            Metadata = new Dictionary<string, object?>
            {
                ["mistral.raw"] = raw
            }
        };

        var mapped = part.ToMistralUnifiedContentPart();

        var mappedNode = Assert.IsType<JsonObject>(mapped);
        Assert.Equal("document_url", mappedNode["type"]?.GetValue<string>());
        Assert.Equal("https://example.com/raw", mappedNode["document_url"]?.GetValue<string>());
    }
}
