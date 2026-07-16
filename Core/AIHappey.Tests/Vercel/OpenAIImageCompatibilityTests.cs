using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace AIHappey.Tests.Vercel;

public sealed class OpenAIImageCompatibilityTests
{
    [Fact]
    public void OpenAI_image_generation_maps_to_vercel_image_request_with_provider_options()
    {
        var request = new OpenAIImageGenerationRequest
        {
            Model = "openai/gpt-image-1.5",
            Prompt = "A cute baby sea otter",
            Background = "transparent",
            Moderation = "low",
            N = 1,
            OutputCompression = 80,
            OutputFormat = "png",
            PartialImages = 1,
            Quality = "medium",
            ResponseFormat = "b64_json",
            Size = "1024x1024",
            Stream = true,
            Style = "vivid",
            User = "user-1234"
        };

        request.ValidateOpenAIImageGenerationRequest();
        var vercelRequest = request.ToImageRequest("gpt-image-1.5", "openai");

        Assert.Equal("gpt-image-1.5", vercelRequest.Model);
        Assert.Equal("A cute baby sea otter", vercelRequest.Prompt);
        Assert.Equal("1024x1024", vercelRequest.Size);
        Assert.Equal(1, vercelRequest.N);

        var providerOptions = vercelRequest.ProviderOptions!["openai"];
        Assert.Equal("transparent", providerOptions.GetProperty("background").GetString());
        Assert.Equal("low", providerOptions.GetProperty("moderation").GetString());
        Assert.Equal(80, providerOptions.GetProperty("output_compression").GetInt32());
        Assert.Equal("png", providerOptions.GetProperty("output_format").GetString());
        Assert.Equal(1, providerOptions.GetProperty("partial_images").GetInt32());
        Assert.True(providerOptions.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OpenAI_image_edit_multipart_form_maps_to_vercel_image_request()
    {
        var form = CreateForm(new Dictionary<string, StringValues>
        {
            ["model"] = "openai/gpt-image-1.5",
            ["prompt"] = "Add a watercolor effect",
            ["background"] = "transparent",
            ["input_fidelity"] = "high",
            ["n"] = "1",
            ["output_format"] = "webp",
            ["quality"] = "high",
            ["size"] = "1024x1024",
            ["stream"] = "true"
        });

        var request = form.ToOpenAIImageEditRequest();
        request.ValidateOpenAIImageEditRequest();
        var vercelRequest = await request.ToImageRequest("gpt-image-1.5", "openai");

        Assert.Equal("gpt-image-1.5", vercelRequest.Model);
        Assert.Equal("Add a watercolor effect", vercelRequest.Prompt);
        Assert.Equal("1024x1024", vercelRequest.Size);
        Assert.Single(vercelRequest.Files!);
        Assert.Equal("image/png", vercelRequest.Files!.Single().MediaType);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("fake image")), vercelRequest.Files!.Single().Data);

        var providerOptions = vercelRequest.ProviderOptions!["openai"];
        Assert.Equal("high", providerOptions.GetProperty("input_fidelity").GetString());
        Assert.Equal("webp", providerOptions.GetProperty("output_format").GetString());
        Assert.True(providerOptions.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OpenAI_image_variation_multipart_form_maps_to_vercel_image_request()
    {
        var form = CreateForm(new Dictionary<string, StringValues>
        {
            ["model"] = "openai/dall-e-2",
            ["n"] = "2",
            ["response_format"] = "url",
            ["size"] = "1024x1024",
            ["user"] = "user-1234"
        });

        var request = form.ToOpenAIImageVariationRequest();
        request.ValidateOpenAIImageVariationRequest();
        var vercelRequest = await request.ToImageRequest("dall-e-2", "openai");

        Assert.Equal("dall-e-2", vercelRequest.Model);
        Assert.Equal("Create a variation of the provided image.", vercelRequest.Prompt);
        Assert.Equal(2, vercelRequest.N);
        Assert.Single(vercelRequest.Files!);

        var providerOptions = vercelRequest.ProviderOptions!["openai"];
        Assert.Equal("url", providerOptions.GetProperty("response_format").GetString());
        Assert.Equal("user-1234", providerOptions.GetProperty("user").GetString());
    }

    [Fact]
    public void Vercel_image_response_projects_to_openai_images_response_and_stream_events()
    {
        var response = new ImageResponse
        {
            Images = ["data:image/png;base64,ZmFrZQ=="],
            Usage = new ImageUsageData
            {
                InputTokens = 1,
                OutputTokens = 2,
                TotalTokens = 3
            },
            Response = new HeaderResponseData
            {
                ModelId = "openai/gpt-image-1.5",
                Timestamp = DateTime.UnixEpoch
            }
        };

        var request = new OpenAIImageGenerationRequest
        {
            Prompt = "test",
            Background = "transparent",
            OutputFormat = "png",
            Quality = "high",
            Size = "1024x1024"
        };

        var openAiResponse = response.ToOpenAIImagesResponse(request);
        var streamEventJson = JsonSerializer.Serialize(response.ToOpenAIImageGenerationCompletedEvents(request).Single(), JsonSerializerOptions.Web);

        Assert.Equal(0, openAiResponse.Created);
        Assert.Equal("ZmFrZQ==", openAiResponse.Data!.Single().B64Json);
        Assert.Equal(3, openAiResponse.Usage!.TotalTokens);
        Assert.Contains("\"type\":\"image_generation.completed\"", streamEventJson, StringComparison.Ordinal);
        Assert.Contains("\"b64_json\":\"ZmFrZQ==\"", streamEventJson, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenAI_image_request_validation_rejects_invalid_counts()
    {
        var badN = new OpenAIImageGenerationRequest
        {
            Prompt = "test",
            N = 11
        };

        var badPartialImages = new OpenAIImageGenerationRequest
        {
            Prompt = "test",
            PartialImages = 4
        };

        Assert.Throws<ArgumentException>(badN.ValidateOpenAIImageGenerationRequest);
        Assert.Throws<ArgumentException>(badPartialImages.ValidateOpenAIImageGenerationRequest);
    }

    private static FormCollection CreateForm(Dictionary<string, StringValues> fields)
    {
        var bytes = Encoding.UTF8.GetBytes("fake image");
        var file = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "image", "image.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png"
        };

        return new FormCollection(fields, new FormFileCollection { file });
    }
}
