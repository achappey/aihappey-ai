using System.Text.Json;
using System.Text.Json.Nodes;
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
    public void Simple_non_streaming_response_maps_to_sampling_result_minimal_contract()
    {
        var samplingResult = LoadUnifiedResponse().ToSamplingResult();

        Assert.Equal(ExpectedSamplingModel, samplingResult.Model);
        Assert.Equal(Role.Assistant, samplingResult.Role);
        Assert.Equal("stop", samplingResult.StopReason);

        var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(samplingResult.Content));
        Assert.Equal(ExpectedText, textBlock.Text);

        var meta = ToJsonElement(samplingResult.Meta);
        Assert.False(meta.TryGetProperty("metadata", out _));
        Assert.False(meta.TryGetProperty("inputTokens", out _));
        Assert.False(meta.TryGetProperty("outputTokens", out _));
        Assert.False(meta.TryGetProperty("totalTokens", out _));

        var usage = meta.GetProperty("usage");
        Assert.Equal(170, usage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(12, usage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(182, usage.GetProperty("totalTokens").GetInt32());
    }

    [Theory]
    [MemberData(nameof(EligibleNonStreamingSamplingFixtures))]
    public void Eligible_non_streaming_response_fixtures_map_to_sampling_with_prefixed_model_and_normalized_usage(string fixturePath)
    {
        var samplingResult = LoadUnifiedResponse(fixturePath).ToSamplingResult();
        var meta = ToJsonElement(samplingResult.Meta);

        Assert.StartsWith($"{ProviderId}/", samplingResult.Model, StringComparison.Ordinal);
        Assert.False(meta.TryGetProperty("metadata", out _));
        Assert.False(meta.TryGetProperty("inputTokens", out _));
        Assert.False(meta.TryGetProperty("outputTokens", out _));
        Assert.False(meta.TryGetProperty("totalTokens", out _));

        var usage = meta.GetProperty("usage");
        Assert.True(usage.TryGetProperty("promptTokens", out _));
        Assert.True(usage.TryGetProperty("completionTokens", out _));
        Assert.True(usage.TryGetProperty("totalTokens", out _));
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

    [Fact]
    public void Openai_image_output_non_streaming_maps_to_sampling_image_without_empty_text_blocks()
    {
        var samplingResult = LoadUnifiedResponse(OpenAiImageResponseFixturePath).ToSamplingResult();

        Assert.Equal("openai/gpt-5.4-mini-2026-03-17", samplingResult.Model);
        Assert.Equal(Role.Assistant, samplingResult.Role);
        Assert.Equal("stop", samplingResult.StopReason);

        var imageBlock = Assert.IsType<ImageContentBlock>(Assert.Single(samplingResult.Content));
        Assert.Equal("image/png", imageBlock.MimeType);
        Assert.NotEmpty(imageBlock.Data.ToArray());

        Assert.DoesNotContain(samplingResult.Content, block =>
            block is TextContentBlock textBlock && string.IsNullOrWhiteSpace(textBlock.Text));

        var meta = ToJsonElement(samplingResult.Meta);
        Assert.False(meta.TryGetProperty("metadata", out _));

        var usage = meta.GetProperty("usage");
        Assert.Equal(1651, usage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(57, usage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(1708, usage.GetProperty("totalTokens").GetInt32());
    }

    [Fact]
    public void Legacy_sampling_meta_roundtrips_to_root_gateway_and_normalized_usage()
    {
        var legacySamplingResult = new CreateMessageResult
        {
            Model = "gpt-5.4-mini-2026-03-17",
            StopReason = "stop",
            Role = Role.Assistant,
            Content = [new TextContentBlock { Text = "Alkmaar" }],
            Meta = new JsonObject
            {
                ["metadata"] = new JsonObject
                {
                    ["gateway"] = new JsonObject
                    {
                        ["cost"] = 0.00020700m
                    }
                },
                ["inputTokens"] = 12,
                ["outputTokens"] = 44,
                ["totalTokens"] = 56
            }
        };

        var unifiedResponse = legacySamplingResult.ToUnifiedResponse(ProviderId);
        var unifiedUsage = ToJsonElement(unifiedResponse.Usage);

        Assert.Equal(12, unifiedUsage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(44, unifiedUsage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(56, unifiedUsage.GetProperty("totalTokens").GetInt32());

        var samplingResult = unifiedResponse.ToSamplingResult();
        var meta = ToJsonElement(samplingResult.Meta);

        Assert.Equal("openai/gpt-5.4-mini-2026-03-17", samplingResult.Model);
        Assert.False(meta.TryGetProperty("metadata", out _));
        Assert.False(meta.TryGetProperty("inputTokens", out _));
        Assert.False(meta.TryGetProperty("outputTokens", out _));
        Assert.False(meta.TryGetProperty("totalTokens", out _));

        var gateway = meta.GetProperty("gateway");
        Assert.Equal(0.00020700m, gateway.GetProperty("cost").GetDecimal());

        var usage = meta.GetProperty("usage");
        Assert.Equal(12, usage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(44, usage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(56, usage.GetProperty("totalTokens").GetInt32());
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
