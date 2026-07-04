using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.OpenRouter;

public sealed class OpenRouterVideoPayloadTests
{
    [Fact]
    public void BuildVideoPayloadMapsInputReferencesToOpenRouterReferences()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "google/veo-3.1",
            Prompt = "preserve the product design",
            Image = Image("image/png", "fallback-image"),
            InputReferences =
            [
                Image("image/png", "reference-one"),
                Image("image/jpeg", "https://example.com/reference-two.jpg")
            ]
        });

        var references = payload.GetProperty("input_references").EnumerateArray().ToList();

        Assert.Equal(2, references.Count);
        Assert.Equal("image_url", references[0].GetProperty("type").GetString());
        Assert.Equal("data:image/png;base64,reference-one", references[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("https://example.com/reference-two.jpg", references[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.DoesNotContain(references, reference =>
            reference.GetProperty("image_url").GetProperty("url").GetString()?.Contains("fallback-image", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void BuildVideoPayloadUsesTopLevelImageAsFallbackReferenceOnlyWhenInputReferencesAreMissing()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "google/veo-3.1",
            Prompt = "animate this image",
            Image = Image("image/webp", "data:image/webp;base64,webp-reference")
        });

        var reference = payload.GetProperty("input_references").EnumerateArray().Single();

        Assert.Equal("image_url", reference.GetProperty("type").GetString());
        Assert.Equal("data:image/webp;base64,webp-reference", reference.GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildVideoPayloadMapsFrameImagesToOpenRouterFrameImages()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "alibaba/wan-2.7",
            Prompt = "interpolate between these frames",
            FrameImages =
            [
                new VideoFrameImage { FrameType = "firstFrame", Image = Image("image/png", "first-frame") },
                new VideoFrameImage { FrameType = "last", Image = Image("image/png", "https://example.com/last-frame.png") }
            ]
        });

        var frameImages = payload.GetProperty("frame_images").EnumerateArray().ToList();

        Assert.Equal(2, frameImages.Count);
        Assert.Equal("first_frame", frameImages[0].GetProperty("frame_type").GetString());
        Assert.Equal("data:image/png;base64,first-frame", frameImages[0].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("last_frame", frameImages[1].GetProperty("frame_type").GetString());
        Assert.Equal("https://example.com/last-frame.png", frameImages[1].GetProperty("image_url").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildVideoPayloadRejectsUnsupportedFrameType()
    {
        var request = new VideoRequest
        {
            Model = "alibaba/wan-2.7",
            Prompt = "bad frame type",
            FrameImages =
            [
                new VideoFrameImage { FrameType = "middle_frame", Image = Image("image/png", "middle-frame") }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => BuildPayload(request));

        Assert.Contains("frameType", exception.Message);
        Assert.Contains("first_frame", exception.Message);
        Assert.Contains("last_frame", exception.Message);
    }

    [Fact]
    public void BuildVideoPayloadLetsOpenRouterProviderOptionsOverrideStandardFields()
    {
        using var providerOptionsDoc = JsonDocument.Parse("""
        {
          "openrouter": {
            "input_references": [
              {
                "type": "image_url",
                "image_url": {
                  "url": "https://example.com/override.png"
                }
              }
            ]
          }
        }
        """);

        var payload = BuildPayload(new VideoRequest
        {
            Model = "google/veo-3.1",
            Prompt = "provider override",
            InputReferences = [Image("image/png", "standard-reference")],
            ProviderOptions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                providerOptionsDoc.RootElement.GetRawText(), JsonSerializerOptions.Web)
        });

        var reference = payload.GetProperty("input_references").EnumerateArray().Single();

        Assert.Equal("https://example.com/override.png", reference.GetProperty("image_url").GetProperty("url").GetString());
    }

    private static JsonElement BuildPayload(VideoRequest request)
    {
        var method = typeof(OpenRouterProvider).GetMethod("BuildOpenRouterVideoPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(OpenRouterProvider), "BuildOpenRouterVideoPayload");

        try
        {
            var payload = method.Invoke(null, [request])!;
            return JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static VideoFile Image(string mediaType, string data)
        => new()
        {
            MediaType = mediaType,
            Data = data
        };
}
