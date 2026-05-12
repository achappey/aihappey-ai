using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Sampling.Mapping;
using AIHappey.Tests.TestInfrastructure;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Sampling;

public sealed class SamplingUnifiedMapperRequestTests
{
    private const string SamplingRequestWithImageFixturePath = "Fixtures/sampling/raw/sampling-request-with-image.json";
    private const string MessagesWithWebSearchFixturePath = "Fixtures/messages/typed/messages-with-websearch-non-streaming.json";
    private const string ProviderId = "openai";
    private const string AnthropicProviderId = "anthropic";
    private const string ExpectedModelWithoutProviderPrefix = "gpt-5.4-mini";
    private const string ExpectedImageMimeType = "image/png";
    private const string ExpectedUserText = "What is in this image?";
    private const int ExpectedMaxTokens = 4096;

    [Fact]
    public void Sampling_request_with_image_maps_to_chat_completions_request_contract()
    {
        var (samplingRequest, fixture) = LoadSamplingRequestFixture();
        var chatCompletionOptions = samplingRequest
            .ToUnifiedRequest(ProviderId)
            .ToChatCompletionOptions(ProviderId);

        Assert.Equal(ExpectedModelWithoutProviderPrefix, chatCompletionOptions.Model);

        var serialized = JsonSerializer.SerializeToElement(chatCompletionOptions, JsonSerializerOptions.Web);
        Assert.Equal(ExpectedMaxTokens, serialized.GetProperty("max_tokens").GetInt32());

        var message = Assert.Single(chatCompletionOptions.Messages);
        Assert.Equal("user", message.Role);
        Assert.Equal(JsonValueKind.Array, message.Content.ValueKind);

        var contentParts = message.Content.EnumerateArray().ToList();
        Assert.Equal(2, contentParts.Count);

        var expectedImageBase64 = GetExpectedImageBase64(fixture);
        var expectedImageDataUrl = ToDataUrl(ExpectedImageMimeType, expectedImageBase64);

        Assert.Equal("image_url", contentParts[0].GetProperty("type").GetString());
        Assert.Equal(expectedImageDataUrl, contentParts[0].GetProperty("image_url").GetProperty("url").GetString());

        Assert.Equal("text", contentParts[1].GetProperty("type").GetString());
        Assert.Equal(ExpectedUserText, contentParts[1].GetProperty("text").GetString());
    }

    [Fact]
    public void Sampling_request_with_image_maps_to_responses_request_contract()
    {
        var (samplingRequest, fixture) = LoadSamplingRequestFixture();
        var responseRequest = samplingRequest
            .ToUnifiedRequest(ProviderId)
            .ToResponseRequest(ProviderId);

        Assert.Equal(ExpectedModelWithoutProviderPrefix, responseRequest.Model);
        Assert.Equal(ExpectedMaxTokens, responseRequest.MaxOutputTokens);

        var inputItems = Assert.IsAssignableFrom<IReadOnlyList<ResponseInputItem>>(responseRequest.Input?.Items);
        var message = Assert.IsType<ResponseInputMessage>(Assert.Single(inputItems));

        Assert.Equal(ResponseRole.User, message.Role);
        Assert.True(message.Content.IsParts);

        var contentParts = Assert.IsAssignableFrom<IReadOnlyList<ResponseContentPart>>(message.Content.Parts);
        Assert.Equal(2, contentParts.Count);

        var expectedImageBase64 = GetExpectedImageBase64(fixture);
        var expectedImageDataUrl = ToDataUrl(ExpectedImageMimeType, expectedImageBase64);

        var imagePart = Assert.IsType<InputImagePart>(contentParts[0]);
        Assert.Equal(expectedImageDataUrl, imagePart.ImageUrl);

        var textPart = Assert.IsType<InputTextPart>(contentParts[1]);
        Assert.Equal(ExpectedUserText, textPart.Text);
    }

    [Fact]
    public void Sampling_request_with_image_maps_to_messages_request_contract()
    {
        var (samplingRequest, fixture) = LoadSamplingRequestFixture();
        var messagesRequest = samplingRequest
            .ToUnifiedRequest(ProviderId)
            .ToMessagesRequest(ProviderId);

        Assert.Equal(ExpectedModelWithoutProviderPrefix, messagesRequest.Model);
        Assert.Equal(ExpectedMaxTokens, messagesRequest.MaxTokens);

        var message = Assert.Single(messagesRequest.Messages);
        Assert.Equal("user", message.Role);

        var contentBlocks = Assert.IsAssignableFrom<IReadOnlyList<MessageContentBlock>>(message.Content.Blocks);
        Assert.Equal(2, contentBlocks.Count);

        var expectedImageBase64 = GetExpectedImageBase64(fixture);

        var imageBlock = contentBlocks[0];

        Assert.Equal("image", imageBlock.Type);
        Assert.NotNull(imageBlock.Source);
        Assert.Equal("base64", imageBlock.Source!.Type);
        Assert.Equal(ExpectedImageMimeType, imageBlock.Source.MediaType);
        Assert.Equal(expectedImageBase64, imageBlock.Source.Data);

        var textBlock = contentBlocks[1];

        Assert.Equal("text", textBlock.Type);
        Assert.Equal(ExpectedUserText, textBlock.Text);
    }

    [Fact]
    public void Messages_response_with_web_search_maps_to_single_sampling_text_block_with_markdown_sources()
    {
        var messagesResponse = LoadMessagesResponseFixture(MessagesWithWebSearchFixturePath);
        var unifiedResponse = messagesResponse.ToUnifiedResponse(AnthropicProviderId);
        Assert.True(unifiedResponse.Output?.Items?.Any(item => item.Type == "source-url") == true);

        var samplingResult = unifiedResponse.ToSamplingResult();

        Assert.Equal("claude-haiku-4-5-20251001", samplingResult.Model);
        Assert.Equal(Role.Assistant, samplingResult.Role);
        Assert.Equal("endTurn", samplingResult.StopReason);

        var textBlock = Assert.IsType<TextContentBlock>(Assert.Single(samplingResult.Content));
        var text = textBlock.Text.ReplaceLineEndings("\n");

        Assert.StartsWith("Ik zal voor je zoeken naar het laatste nieuws over de oorlog.", text, StringComparison.Ordinal);
        Assert.Contains("Hier is het laatste nieuws over de oorlog:", text, StringComparison.Ordinal);
        Assert.Contains("## Huidige Situatie", text, StringComparison.Ordinal);
        Assert.Contains("Gesprekken zullen waarschijnlijk geen aanzienlijke vooruitgang boeken", text, StringComparison.Ordinal);

        Assert.Contains("[Iran war live: President Trump calls Iranian peace proposal ‘garbage’ | US-Israel war on Iran News | Al Jazeera](https://www.aljazeera.com/news/liveblog/2026/5/12/iran-war-live-trump-slams-iranian-proposal-as-ceasefire-hangs-by-a-thread)", text, StringComparison.Ordinal);
        Assert.Contains("[Day 72 of Middle East conflict — Trump calls Iran response to US proposal ‘totally unacceptable’ | CNN](https://www.cnn.com/2026/05/10/world/live-news/iran-war-news)", text, StringComparison.Ordinal);
        Assert.Contains("[Live updates: Trump says ceasefire with Iran on ‘massive life support’ after he rejects Tehran’s proposal | CNN](https://www.cnn.com/2026/05/11/world/live-news/iran-war-proposal-trump)", text, StringComparison.Ordinal);

        Assert.Equal(1, CountOccurrences(text, "https://www.cnn.com/2026/05/10/world/live-news/iran-war-news"));
        Assert.Equal(1, CountOccurrences(text, "https://www.cnn.com/2026/05/11/world/live-news/iran-war-proposal-trump"));
        Assert.DoesNotContain("\nIran war live: President Trump calls Iranian peace proposal", text, StringComparison.Ordinal);

        var meta = ToJsonElement(samplingResult.Meta);
        var usage = meta.GetProperty("usage");
        Assert.Equal(17541, usage.GetProperty("promptTokens").GetInt32());
        Assert.Equal(568, usage.GetProperty("completionTokens").GetInt32());
        Assert.Equal(18109, usage.GetProperty("totalTokens").GetInt32());
    }

    private static (CreateMessageRequestParams Request, JsonElement Fixture) LoadSamplingRequestFixture()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(SamplingRequestWithImageFixturePath));
        var fixture = JsonDocument.Parse(json).RootElement.Clone();

        var request = JsonSerializer.Deserialize<CreateMessageRequestParams>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize sampling fixture from [{SamplingRequestWithImageFixturePath}](Core/AIHappey.Tests/{SamplingRequestWithImageFixturePath}).");

        return (request, fixture);
    }

    private static MessagesResponse LoadMessagesResponseFixture(string fixturePath)
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(fixturePath));

        return JsonSerializer.Deserialize<MessagesResponse>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize messages fixture from [{fixturePath}](Core/AIHappey.Tests/{fixturePath}).");
    }

    private static string GetExpectedImageBase64(JsonElement fixture)
        => fixture.GetProperty("messages")[0].GetProperty("content").GetProperty("data").GetString()
           ?? throw new InvalidOperationException($"Fixture [{SamplingRequestWithImageFixturePath}](Core/AIHappey.Tests/{SamplingRequestWithImageFixturePath}) does not contain an image payload.");

    private static string ToDataUrl(string mimeType, string base64)
        => $"data:{mimeType};base64,{base64}";

    private static JsonElement ToJsonElement(object? value)
        => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;

        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
