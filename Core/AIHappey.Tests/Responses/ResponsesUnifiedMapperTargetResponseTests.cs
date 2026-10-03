using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Responses;

public sealed class ResponsesUnifiedMapperTargetResponseTests
{
    private const string SimpleResponseFixturePath = "Fixtures/responses/raw/simple-response-non-streaming.json";
    private const string OpenAiImageResponseFixturePath = "Fixtures/responses/raw/openai-image-output-non-streaming.json";
    private const string GroqReasoningResponseFixturePath = "Fixtures/responses/raw/groq-with-reasoning-non-streaming.json";
    private const string ProviderId = "openai";
    private const string ExpectedModel = "gpt-5.4-nano-2026-03-17";
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

    [Fact]
    public void Mistral_usage_maps_to_typed_responses_usage_and_preserves_provider_extensions_and_raw_metadata()
    {
        var rawUsage = JsonSerializer.SerializeToElement(new
        {
            prompt_tokens = 734,
            completion_tokens = 1555,
            total_tokens = 19779,
            input_tokens = 734,
            output_tokens = 1555
        });
        var response = new AIResponse
        {
            ProviderId = "mistral",
            Model = "mistral/mistral-medium-latest",
            Status = "completed",
            Usage = rawUsage
        }.ToResponseResult();

        var typedUsage = Assert.IsType<ResponseUsage>(response.Usage);
        Assert.Same(typedUsage, response.NormalizedUsage);
        Assert.Equal(734, typedUsage.InputTokens);
        Assert.Equal(1555, typedUsage.OutputTokens);
        Assert.Equal(19779, typedUsage.TotalTokens);
        Assert.Equal(734, typedUsage.AdditionalProperties!["prompt_tokens"].GetInt32());
        Assert.Equal(1555, typedUsage.AdditionalProperties["completion_tokens"].GetInt32());

        var json = JsonSerializer.SerializeToElement(response, ResponseJson.Default);
        var usage = json.GetProperty("usage");
        Assert.Equal(734, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(1555, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(19779, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(734, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(1555, usage.GetProperty("completion_tokens").GetInt32());

        var metadata = json.GetProperty("metadata");
        Assert.Equal(734, metadata.GetProperty("mistral").GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(rawUsage.GetRawText(), metadata.GetProperty("mistral").GetProperty("usage").GetRawText());
    }

    [Fact]
    public void Anthropic_usage_maps_to_typed_responses_usage_and_preserves_provider_extensions_and_raw_details()
    {
        var rawUsage = JsonSerializer.SerializeToElement(new
        {
            input_tokens = 47518,
            output_tokens = 1044,
            cache_creation_input_tokens = 17,
            cache_read_input_tokens = 23,
            output_tokens_details = new { thinking_tokens = 83 },
            service_tier = "standard"
        });
        var response = new AIResponse
        {
            ProviderId = "anthropic",
            Model = "anthropic/claude-haiku",
            Status = "completed",
            Usage = rawUsage
        }.ToResponseResult();

        var typedUsage = Assert.IsType<ResponseUsage>(response.Usage);
        Assert.Same(typedUsage, response.NormalizedUsage);
        Assert.Equal(47518, typedUsage.InputTokens);
        Assert.Equal(1044, typedUsage.OutputTokens);
        Assert.Equal(48562, typedUsage.TotalTokens);
        Assert.Equal(23, typedUsage.InputTokensDetails!.CachedTokens);
        Assert.Equal(17, typedUsage.InputTokensDetails.CacheWriteTokens);
        Assert.Equal(83, typedUsage.OutputTokensDetails!.ReasoningTokens);
        Assert.Equal("standard", typedUsage.AdditionalProperties!["service_tier"].GetString());

        var json = JsonSerializer.SerializeToElement(response, ResponseJson.Default);
        var usage = json.GetProperty("usage");
        Assert.Equal(47518, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(1044, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(48562, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(23, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(17, usage.GetProperty("input_tokens_details").GetProperty("cache_write_tokens").GetInt32());
        Assert.Equal(83, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
        Assert.Equal("standard", usage.GetProperty("service_tier").GetString());
        Assert.Equal(17, usage.GetProperty("cache_creation_input_tokens").GetInt32());
        Assert.Equal(23, usage.GetProperty("cache_read_input_tokens").GetInt32());

        var raw = json.GetProperty("metadata").GetProperty("anthropic").GetProperty("usage");
        Assert.Equal("standard", raw.GetProperty("service_tier").GetString());
        Assert.Equal(83, raw.GetProperty("output_tokens_details").GetProperty("thinking_tokens").GetInt32());
        Assert.Equal(rawUsage.GetRawText(), raw.GetRawText());
    }

    [Fact]
    public void Google_interactions_usage_maps_to_typed_responses_usage_and_preserves_provider_extensions_and_raw_metadata()
    {
        var rawUsage = JsonSerializer.SerializeToElement(new
        {
            total_input_tokens = 8438,
            total_output_tokens = 398,
            total_cached_tokens = 31,
            total_thought_tokens = 19,
            total_tokens = 8836,
            input_tokens_by_modality = new[] { new { modality = "text", tokens = 8438 } }
        });
        var response = new AIResponse
        {
            ProviderId = "google",
            Model = "google/gemini",
            Status = "completed",
            Usage = rawUsage
        }.ToResponseResult();

        var typedUsage = Assert.IsType<ResponseUsage>(response.Usage);
        Assert.Same(typedUsage, response.NormalizedUsage);
        Assert.Equal(8438, typedUsage.InputTokens);
        Assert.Equal(398, typedUsage.OutputTokens);
        Assert.Equal(8836, typedUsage.TotalTokens);
        Assert.Equal(31, typedUsage.InputTokensDetails!.CachedTokens);
        Assert.Equal(19, typedUsage.OutputTokensDetails!.ReasoningTokens);
        Assert.Equal(8438, typedUsage.AdditionalProperties!["total_input_tokens"].GetInt32());

        var json = JsonSerializer.SerializeToElement(response, ResponseJson.Default);
        var usage = json.GetProperty("usage");
        Assert.Equal(8438, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(398, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(8836, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(31, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.Equal(19, usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32());
        Assert.Equal(8438, usage.GetProperty("total_input_tokens").GetInt32());
        Assert.Equal(398, usage.GetProperty("total_output_tokens").GetInt32());
        Assert.Equal(31, usage.GetProperty("total_cached_tokens").GetInt32());
        Assert.Equal(19, usage.GetProperty("total_thought_tokens").GetInt32());
        var modality = Assert.Single(usage.GetProperty("input_tokens_by_modality").EnumerateArray());
        Assert.Equal("text", modality.GetProperty("modality").GetString());
        Assert.Equal(8438, modality.GetProperty("tokens").GetInt32());

        var raw = json.GetProperty("metadata").GetProperty("google").GetProperty("usage");
        Assert.Equal(8438, raw.GetProperty("total_input_tokens").GetInt32());
        Assert.Equal(rawUsage.GetRawText(), raw.GetRawText());
    }

    [Fact]
    public void Perplexity_usage_maps_to_typed_responses_usage_and_preserves_cost_extensions_and_raw_metadata()
    {
        var rawUsage = JsonSerializer.SerializeToElement(new
        {
            prompt_tokens = 120,
            completion_tokens = 30,
            total_tokens = 150,
            search_context_size = "medium",
            cost = new { total_cost = 0.0042m }
        });
        var response = new AIResponse
        {
            ProviderId = "perplexity",
            Model = "perplexity/sonar",
            Status = "completed",
            Usage = rawUsage
        }.ToResponseResult();

        var typedUsage = Assert.IsType<ResponseUsage>(response.Usage);
        Assert.Same(typedUsage, response.NormalizedUsage);
        Assert.Equal(120, typedUsage.InputTokens);
        Assert.Equal(30, typedUsage.OutputTokens);
        Assert.Equal(150, typedUsage.TotalTokens);
        Assert.Equal("medium", typedUsage.AdditionalProperties!["search_context_size"].GetString());
        Assert.Equal(0.0042m, typedUsage.AdditionalProperties["cost"].GetProperty("total_cost").GetDecimal());

        var json = JsonSerializer.SerializeToElement(response, ResponseJson.Default);
        var usage = json.GetProperty("usage");
        Assert.Equal(120, usage.GetProperty("input_tokens").GetInt32());
        Assert.Equal(30, usage.GetProperty("output_tokens").GetInt32());
        Assert.Equal(150, usage.GetProperty("total_tokens").GetInt32());
        Assert.Equal(120, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(30, usage.GetProperty("completion_tokens").GetInt32());
        Assert.Equal("medium", usage.GetProperty("search_context_size").GetString());
        Assert.Equal(0.0042m, usage.GetProperty("cost").GetProperty("total_cost").GetDecimal());
        var raw = json.GetProperty("metadata").GetProperty("perplexity").GetProperty("usage");
        Assert.Equal("medium", raw.GetProperty("search_context_size").GetString());
        Assert.Equal(0.0042m, raw.GetProperty("cost").GetProperty("total_cost").GetDecimal());
        Assert.Equal(rawUsage.GetRawText(), raw.GetRawText());
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
