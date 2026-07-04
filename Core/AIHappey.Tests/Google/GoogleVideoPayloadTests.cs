using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Google;

public sealed class GoogleVideoPayloadTests
{
    [Fact]
    public void BuildVideoPayloadMapsTopLevelImageToPrimaryImage()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "animate this image",
            Image = Image("image/png", "primary-base64")
        });

        var instance = FirstInstance(payload);
        var inlineData = instance.GetProperty("image").GetProperty("inlineData");

        Assert.Equal("image/png", inlineData.GetProperty("mimeType").GetString());
        Assert.Equal("primary-base64", inlineData.GetProperty("data").GetString());
        Assert.False(instance.TryGetProperty("referenceImages", out _));
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildVideoPayloadMapsInputReferencesToReferenceImages()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "preserve the product design",
            InputReferences =
            [
                Image("image/png", "reference-one"),
                Image("image/jpeg", "reference-two")
            ]
        });

        var references = FirstInstance(payload).GetProperty("referenceImages").EnumerateArray().ToList();

        Assert.Equal(2, references.Count);
        Assert.Equal("asset", references[0].GetProperty("referenceType").GetString());
        Assert.Equal("reference-one", references[0].GetProperty("image").GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("image/jpeg", references[1].GetProperty("image").GetProperty("inlineData").GetProperty("mimeType").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildVideoPayloadMapsFirstAndLastFrameImages()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "interpolate between these frames",
            FrameImages =
            [
                new VideoFrameImage { FrameType = "first_frame", Image = Image("image/png", "first-frame") },
                new VideoFrameImage { FrameType = "last_frame", Image = Image("image/png", "last-frame") }
            ]
        });

        var instance = FirstInstance(payload);

        Assert.Equal("first-frame", instance.GetProperty("image").GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Equal("last-frame", instance.GetProperty("lastFrame").GetProperty("data").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildVideoPayloadUsesTopLevelImageAsReferenceWhenFirstFrameIsPresent()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "use the frame and preserve the subject",
            Image = Image("image/png", "reference-from-image"),
            FrameImages =
            [
                new VideoFrameImage { FrameType = "first_frame", Image = Image("image/png", "first-frame") }
            ]
        });

        var instance = FirstInstance(payload);
        var references = instance.GetProperty("referenceImages").EnumerateArray().ToList();

        Assert.Equal("first-frame", instance.GetProperty("image").GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Single(references);
        Assert.Equal("reference-from-image", references[0].GetProperty("image").GetProperty("inlineData").GetProperty("data").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildVideoPayloadRejectsMoreThanThreeReferenceImages()
    {
        var request = new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "too many references",
            Image = Image("image/png", "top-level-reference"),
            InputReferences =
            [
                Image("image/png", "reference-one"),
                Image("image/png", "reference-two"),
                Image("image/png", "reference-three")
            ],
            FrameImages =
            [
                new VideoFrameImage { FrameType = "first_frame", Image = Image("image/png", "first-frame") }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => BuildPayload(request));
        Assert.Contains("at most 3 reference images", exception.Message);
    }

    [Fact]
    public void BuildVideoPayloadStripsDataUrlPrefix()
    {
        var (payload, _) = BuildPayload(new VideoRequest
        {
            Model = "veo-3.1-generate-preview",
            Prompt = "animate this image",
            Image = Image("application/octet-stream", "data:image/webp;base64,webp-base64")
        });

        var inlineData = FirstInstance(payload).GetProperty("image").GetProperty("inlineData");

        Assert.Equal("image/webp", inlineData.GetProperty("mimeType").GetString());
        Assert.Equal("webp-base64", inlineData.GetProperty("data").GetString());
    }

    private static (JsonElement Payload, List<object> Warnings) BuildPayload(VideoRequest request)
    {
        var warnings = new List<object>();
        var method = typeof(GoogleAIProvider).GetMethod("BuildVideoPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "BuildVideoPayload");

        try
        {
            var payload = method.Invoke(null, [request, warnings])!;
            return (JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), warnings);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static JsonElement FirstInstance(JsonElement payload)
        => payload.GetProperty("instances").EnumerateArray().Single();

    private static VideoFile Image(string mediaType, string data)
        => new()
        {
            MediaType = mediaType,
            Data = data
        };
}
