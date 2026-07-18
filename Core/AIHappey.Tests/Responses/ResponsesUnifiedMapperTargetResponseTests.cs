using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Sampling.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesUnifiedMapperTargetResponseTests
{
    private const string SimpleResponseFixturePath = "Fixtures/responses/raw/simple-response-non-streaming.json";
    private const string OpenAiImageResponseFixturePath = "Fixtures/responses/raw/openai-image-output-non-streaming.json";
    private const string GroqReasoningResponseFixturePath = "Fixtures/responses/raw/groq-with-reasoning-non-streaming.json";
    private const string ProviderId = "openai";
    private const string ExpectedModel = "gpt-5.4-nano-2026-03-17";
    private const string ExpectedSamplingModel = $"{ProviderId}/{ExpectedModel}";
    private const string ExpectedText = "Welkom Arthur, tijd voor frisse ideeën!";

    public static IEnumerable<object[]> EligibleNonStreamingSamplingFixtures()
    {
        yield return [SimpleResponseFixturePath];
        yield return [OpenAiImageResponseFixturePath];
    }

    [Fact]
    public void Simple_non_streaming_response_maps_to_messages_response_minimal_contract()
    {
        var messagesResponse = LoadUnifiedResponse().ToMessagesResponse();

        Assert.EndsWith(ExpectedModel, messagesResponse.Model);
        Assert.Equal("assistant", messagesResponse.Role);
        Assert.Equal("end_turn", messagesResponse.StopReason);

        var contentBlock = Assert.Single(messagesResponse.Content);
        Assert.Equal("text", contentBlock.Type);
        Assert.Equal(ExpectedText, contentBlock.Text);

        Assert.NotNull(messagesResponse.Usage);
        Assert.Equal(170, messagesResponse.Usage!.InputTokens);
        Assert.Equal(12, messagesResponse.Usage.OutputTokens);
    }

    [Fact]
    public void Groq_non_streaming_reasoning_response_maps_to_unified_reasoning_and_messages_thinking_blocks()
    {
        var unifiedResponse = LoadUnifiedResponse(GroqReasoningResponseFixturePath);

        var outputItems = Assert.IsAssignableFrom<IReadOnlyList<AIOutputItem>>(unifiedResponse.Output?.Items);
        Assert.Equal("reasoning", outputItems[0].Type);

        var reasoningPart = Assert.IsType<AIReasoningContentPart>(Assert.Single(outputItems[0].Content!));
        Assert.Equal("User says \"thanks bro\". Probably respond politely.", reasoningPart.Text);

        var messagesResponse = unifiedResponse.ToMessagesResponse();

        Assert.Equal("groq/openai/gpt-oss-20b", messagesResponse.Model);
        Assert.Equal("assistant", messagesResponse.Role);
        Assert.Equal("end_turn", messagesResponse.StopReason);

        Assert.Collection(
            messagesResponse.Content,
            block =>
            {
                Assert.Equal("thinking", block.Type);
                Assert.Equal("User says \"thanks bro\". Probably respond politely.", block.Thinking);
                Assert.Null(block.Text);
            },
            block =>
            {
                Assert.Equal("text", block.Type);
                Assert.Equal("You’re welcome! Anytime you need help, just let me know. 😎", block.Text);
            });
    }

    [Fact]
    public void Simple_non_streaming_response_maps_to_chat_completion_minimal_contract()
    {
        var chatCompletion = LoadUnifiedResponse().ToChatCompletion();

        Assert.EndsWith(ExpectedModel, chatCompletion.Model);

        var choice = ToJsonElement(Assert.Single(chatCompletion.Choices));
        var message = choice.GetProperty("message");

        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal(ExpectedText, message.GetProperty("content").GetString());

        var usage = ToJsonElement(chatCompletion.Usage);
        Assert.Equal(170, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(182, usage.GetProperty("total_tokens").GetInt32());
    }
   
    private static AIResponse LoadUnifiedResponse(string fixturePath = SimpleResponseFixturePath)
        => LoadResponseFixture(fixturePath).ToUnifiedResponse(ProviderId);

    private static ResponseResult LoadResponseFixture(string fixturePath = SimpleResponseFixturePath)
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(fixturePath));

        return JsonSerializer.Deserialize<ResponseResult>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize response fixture from [{fixturePath}](Core/AIHappey.Tests/{fixturePath}).");
    }

    private static JsonElement ToJsonElement(object? value)
        => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
