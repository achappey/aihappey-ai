using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.ChatCompletions;

public sealed class ChatCompletionsUnifiedMapperRequestTests
{
    private const string ApprovedToolCallWithOutputFixturePath = "Fixtures/api-chat/raw/approved-tool-call-with-output-chatrequest.json";

    [Fact]
    public void Vercel_chat_request_with_client_tool_output_maps_to_assistant_tool_call_followed_by_tool_message()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(ApprovedToolCallWithOutputFixturePath));
        var chatRequest = JsonSerializer.Deserialize<ChatRequest>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize fixture chat request from [{ApprovedToolCallWithOutputFixturePath}](Core/AIHappey.Tests/{ApprovedToolCallWithOutputFixturePath}).");

        var unifiedRequest = chatRequest.ToUnifiedRequest("openai");
        var options = unifiedRequest.ToChatCompletionOptions();
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
}
