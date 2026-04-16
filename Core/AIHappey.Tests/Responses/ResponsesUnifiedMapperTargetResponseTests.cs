using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages;
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
    private const string ProviderId = "openai";
    private const string ExpectedModel = "gpt-5.4-nano-2026-03-17";
    private const string ExpectedText = "Welkom Arthur, tijd voor frisse ideeën!";

    [Fact]
    public void Simple_non_streaming_response_maps_to_sampling_result_minimal_contract()
    {
        var samplingResult = LoadUnifiedResponse().ToSamplingResult();

        Assert.Equal(ExpectedModel, samplingResult.Model);
        Assert.Equal(Role.Assistant, samplingResult.Role);
        Assert.Equal("stop", samplingResult.StopReason);

        var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(samplingResult.Content));
        Assert.Equal(ExpectedText, textBlock.Text);

        var usage = ToJsonElement(samplingResult.Meta);
        Assert.Equal(170, usage.GetProperty("inputTokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("outputTokens").GetInt32());
        Assert.Equal(182, usage.GetProperty("totalTokens").GetInt32());
    }

    [Fact]
    public void Simple_non_streaming_response_maps_to_messages_response_minimal_contract()
    {
        var messagesResponse = LoadUnifiedResponse().ToMessagesResponse();

        Assert.Equal(ExpectedModel, messagesResponse.Model);
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
    public void Simple_non_streaming_response_maps_to_chat_completion_minimal_contract()
    {
        var chatCompletion = LoadUnifiedResponse().ToChatCompletion();

        Assert.Equal(ExpectedModel, chatCompletion.Model);

        var choice = ToJsonElement(Assert.Single(chatCompletion.Choices));
        var message = choice.GetProperty("message");

        Assert.Equal("assistant", message.GetProperty("role").GetString());
        Assert.Equal(ExpectedText, message.GetProperty("content").GetString());

        var usage = ToJsonElement(chatCompletion.Usage);
        Assert.Equal(170, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(182, usage.GetProperty("total_tokens").GetInt32());
    }

    private static AIResponse LoadUnifiedResponse()
        => LoadResponseFixture().ToUnifiedResponse(ProviderId);

    private static ResponseResult LoadResponseFixture()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(SimpleResponseFixturePath));

        return JsonSerializer.Deserialize<ResponseResult>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize response fixture from [{SimpleResponseFixturePath}](Core/AIHappey.Tests/{SimpleResponseFixturePath}).");
    }

    private static JsonElement ToJsonElement(object? value)
        => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);
}
