using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.SpaceXAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIImagePayloadTests
{
    [Theory]
    [InlineData("auto")]
    [InlineData("low")]
    [InlineData("medium")]
    public void BuildPayloadForGenerationForwardsQualityAndNewAspectRatios(string quality)
    {
        var request = Request(quality, "21:9");
        var payload = BuildPayload(request);

        Assert.Equal(quality, payload.GetProperty("quality").GetString());
        Assert.Equal("21:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.False(payload.TryGetProperty("image", out _));
    }

    [Fact]
    public void BuildPayloadForEditAcceptsFiveImagesAndForwardsSettings()
    {
        var request = Request("medium", "5:2");
        request.Files = Enumerable.Range(1, 5)
            .Select(index => new ImageFile
            {
                Type = "file",
                MediaType = "image/png",
                Data = $"image-{index}"
            })
            .ToArray();

        var payload = BuildPayload(request);

        Assert.Equal("medium", payload.GetProperty("quality").GetString());
        Assert.Equal("5:2", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal(5, payload.GetProperty("images").GetArrayLength());
        Assert.Equal("data:image/png;base64,image-1", payload.GetProperty("image").GetProperty("url").GetString());
    }

    [Fact]
    public void BuildPayloadRejectsSixSourceImages()
    {
        var request = Request("auto", "1:1");
        request.Files = Enumerable.Range(1, 6)
            .Select(index => new ImageFile { Type = "file", MediaType = "image/png", Data = $"image-{index}" })
            .ToArray();

        var error = Assert.Throws<ArgumentException>(() => BuildPayload(request));
        Assert.Contains("five source images", error.Message);
    }

    [Theory]
    [InlineData("high")]
    [InlineData("invalid")]
    public void BuildPayloadRejectsUnsupportedQuality(string quality)
    {
        var error = Assert.Throws<ArgumentException>(() => BuildPayload(Request(quality, "1:1")));
        Assert.Contains("auto, low, or medium", error.Message);
    }

    [Fact]
    public void BuildPayloadRejectsQualityForOtherModels()
    {
        var request = Request("auto", "1:1");
        request.Model = "grok-imagine-image-1.0";

        var error = Assert.Throws<ArgumentException>(() => BuildPayload(request));
        Assert.Contains("only supported by grok-imagine-image-2.0", error.Message);
    }

    private static ImageRequest Request(string quality, string aspectRatio)
        => new()
        {
            Model = "grok-imagine-image-2.0",
            Prompt = "A cinematic test image",
            N = 1,
            AspectRatio = aspectRatio,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["spacexai"] = JsonSerializer.SerializeToElement(new { quality })
            }
        };

    private static JsonElement BuildPayload(ImageRequest request)
    {
        var method = typeof(SpaceXAIProvider).GetMethod(
            "BuildXaiImagePayload",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(SpaceXAIProvider), "BuildXaiImagePayload");

        try
        {
            var payload = method.Invoke(null, [request, null])!;
            return JsonSerializer.SerializeToElement(payload, new JsonSerializerOptions
            {
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
            });
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            throw error.InnerException;
        }
    }
}
