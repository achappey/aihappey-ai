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
    private const string ProviderId = "openai";
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
            .ToChatCompletionOptions();

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

    private static (CreateMessageRequestParams Request, JsonElement Fixture) LoadSamplingRequestFixture()
    {
        var json = File.ReadAllText(FixtureFileLoader.ResolveFixturePath(SamplingRequestWithImageFixturePath));
        var fixture = JsonDocument.Parse(json).RootElement.Clone();

        var request = JsonSerializer.Deserialize<CreateMessageRequestParams>(json, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Could not deserialize sampling fixture from [{SamplingRequestWithImageFixturePath}](Core/AIHappey.Tests/{SamplingRequestWithImageFixturePath}).");

        return (request, fixture);
    }

    private static string GetExpectedImageBase64(JsonElement fixture)
        => fixture.GetProperty("messages")[0].GetProperty("content").GetProperty("data").GetString()
           ?? throw new InvalidOperationException($"Fixture [{SamplingRequestWithImageFixturePath}](Core/AIHappey.Tests/{SamplingRequestWithImageFixturePath}) does not contain an image payload.");

    private static string ToDataUrl(string mimeType, string base64)
        => $"data:{mimeType};base64,{base64}";
}
