using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsUnifiedMapperRequestTests
{
    private const string ApprovedToolCallWithOutputFixturePath = "Fixtures/api-chat/raw/approved-tool-call-with-output-chatrequest.json";
    private const string ProviderToolsWithFollowUpFixturePath = "Fixtures/api-chat/raw/provider-tools-with-follow-up-chatrequest.json";

    [Fact]
    public void Vercel_chat_request_with_client_tool_output_maps_to_assistant_tool_call_followed_by_tool_message()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(ApprovedToolCallWithOutputFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{ApprovedToolCallWithOutputFixturePath}](Core/AIHappey.Tests/{ApprovedToolCallWithOutputFixturePath}).");

        var unifiedRequest = chatRequest.ToUnifiedRequest("openai");
        var options = unifiedRequest.ToChatCompletionOptions("openai");
        var messages = options.Messages.ToList();

        Assert.Equal(4, messages.Count);

        Assert.Equal("system", messages[0].Role);
        Assert.Equal("user", messages[1].Role);

        var assistantMessage = messages[2];
        Assert.Equal("assistant", assistantMessage.Role);
        Assert.Equal(JsonValueKind.Null, assistantMessage.Content.ValueKind);

        var assistantToolCall = JsonSerializer.SerializeToElement(Assert.Single(assistantMessage.ToolCalls!), JsonSerializerOptions.Web);
        Assert.Equal("call_xyP61r9U4PJRpE095iQXjn2g", assistantToolCall.GetProperty("id").GetString());
        Assert.Equal("function", assistantToolCall.GetProperty("type").GetString());
        Assert.Equal("github_rest_countries_search_codes", assistantToolCall.GetProperty("function").GetProperty("name").GetString());
        var rawArguments = assistantToolCall.GetProperty("function").GetProperty("arguments").GetString();
        Assert.NotNull(rawArguments);

        using (var argumentsDocument = JsonDocument.Parse(rawArguments!))
        {
            Assert.Equal("Poland", argumentsDocument.RootElement.GetProperty("name").GetString());
        }

        var toolMessage = messages[3];
        Assert.Equal("tool", toolMessage.Role);
        Assert.Equal("call_xyP61r9U4PJRpE095iQXjn2g", toolMessage.ToolCallId);
        Assert.Equal(JsonValueKind.String, toolMessage.Content.ValueKind);

        var serializedToolOutput = toolMessage.Content.GetString();
        Assert.NotNull(serializedToolOutput);

        using var toolOutput = JsonDocument.Parse(serializedToolOutput!);
        var resource = toolOutput.RootElement.GetProperty("content")[0].GetProperty("resource");
        Assert.Equal("https://github.com/egbakou/RESTCountries.NET", resource.GetProperty("uri").GetString());
        Assert.Equal("application/json", resource.GetProperty("mimeType").GetString());
        Assert.Equal("[{\"common\":\"Poland\",\"official\":\"Republic of Poland\",\"cca2\":\"PL\"}]", resource.GetProperty("text").GetString());
    }

    [Fact]
    public void Provider_executed_tool_output_from_saved_ui_chatrequest_is_unwrapped_back_to_semantic_unified_output()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(ProviderToolsWithFollowUpFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{ProviderToolsWithFollowUpFixturePath}](Core/AIHappey.Tests/{ProviderToolsWithFollowUpFixturePath}).");

        var unifiedRequest = chatRequest.ToUnifiedRequest("anthropic");
        var assistantItem = Assert.Single(unifiedRequest.Input?.Items?.Where(item => item.Role == "assistant") ?? []);
        var toolPart = Assert.Single(assistantItem.Content?.OfType<AIToolCallContentPart>() ?? []);

        Assert.True(toolPart.ProviderExecuted);

        var output = JsonSerializer.SerializeToElement(toolPart.Output, JsonSerializerOptions.Web);
        Assert.False(output.TryGetProperty("structuredContent", out _));

        var content = output.GetProperty("content");
        Assert.True(content.GetArrayLength() >= 1);
        Assert.Equal("web_search_result", content[0].GetProperty("type").GetString());
        Assert.Equal("https://www.aljazeera.com/news/liveblog/2026/4/16/iran-war-live-pakistan-in-push-for-new-round-of-us-iran-peace-negotiations", content[3].GetProperty("url").GetString());
    }

    [Fact]
    public void Provider_executed_raw_tool_output_is_wrapped_only_at_ui_boundary_and_unwrapped_on_return_to_unified()
    {
        var rawOutput = JsonSerializer.SerializeToElement(new
        {
            search_results = new[]
            {
                new
                {
                    title = "Result 1",
                    url = "https://example.com/result-1"
                }
            }
        }, JsonSerializerOptions.Web);

        var uiMessage = VercelUnifiedMapper.ToUIMessage(
            new AIOutputItem
            {
                Role = "assistant",
                Content =
                [
                    new AIToolCallContentPart
                    {
                        Type = "tool-web_search",
                        ToolCallId = "srvtoolu_test_1",
                        ToolName = "web_search",
                        Title = "web_search",
                        State = "output-available",
                        Output = rawOutput,
                        ProviderExecuted = true
                    }
                ]
            },
            id: "assistant-msg");

        var invocation = Assert.IsType<ToolInvocationPart>(Assert.Single(uiMessage.Parts));
        var uiOutput = JsonSerializer.SerializeToElement(invocation.Output, JsonSerializerOptions.Web);
        var structuredContent = uiOutput.GetProperty("structuredContent");

        Assert.Equal("Result 1", structuredContent.GetProperty("search_results")[0].GetProperty("title").GetString());

        var request = new ChatRequest
        {
            Model = "anthropic/test-model",
            Messages = [uiMessage]
        };

        var roundTrippedToolPart = Assert.Single(
                Assert.Single(request.ToUnifiedRequest("anthropic").Input?.Items ?? []).Content?.OfType<AIToolCallContentPart>()
                ?? []) ;

        var roundTrippedOutput = JsonSerializer.SerializeToElement(roundTrippedToolPart.Output, JsonSerializerOptions.Web);
        Assert.False(roundTrippedOutput.TryGetProperty("structuredContent", out _));
        Assert.Equal("Result 1", roundTrippedOutput.GetProperty("search_results")[0].GetProperty("title").GetString());
    }
}
