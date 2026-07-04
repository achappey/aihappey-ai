using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.xAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.XAI;

public sealed class XAIVideoPayloadTests
{
    [Fact]
    public void BuildXaiVideoPayloadMapsTopLevelImageToDocumentedImageObject()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video",
            Prompt = "animate this image",
            Image = Image("image/png", "primary-base64")
        });

        var image = payload.GetProperty("image");

        Assert.Equal("data:image/png;base64,primary-base64", image.GetProperty("url").GetString());
        Assert.False(payload.TryGetProperty("reference_images", out _));
    }

    [Fact]
    public void BuildXaiVideoPayloadMapsInputReferencesToReferenceImages()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video",
            Prompt = "use these references",
            InputReferences =
            [
                Image("image/png", "reference-one"),
                Image("image/jpeg", "https://example.com/reference-two.jpg")
            ]
        });

        var references = payload.GetProperty("reference_images").EnumerateArray().ToList();

        Assert.Equal(2, references.Count);
        Assert.Equal("data:image/png;base64,reference-one", references[0].GetProperty("url").GetString());
        Assert.Equal("https://example.com/reference-two.jpg", references[1].GetProperty("url").GetString());
        Assert.False(payload.TryGetProperty("image", out _));
    }

    [Fact]
    public void BuildXaiVideoPayloadPreservesDataUrlInputs()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video",
            Prompt = "use data urls",
            Image = Image("application/octet-stream", "data:image/webp;base64,webp-base64"),
            InputReferences =
            [
                Image("application/octet-stream", "data:image/jpeg;base64,jpeg-base64")
            ]
        });

        var reference = payload.GetProperty("reference_images").EnumerateArray().Single();

        Assert.Equal("data:image/webp;base64,webp-base64", payload.GetProperty("image").GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,jpeg-base64", reference.GetProperty("url").GetString());
    }

    [Fact]
    public void BuildXaiVideoPayloadUsesPngDataUrlWhenMediaTypeIsMissing()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video",
            Prompt = "default media type",
            Image = Image(null, "raw-base64")
        });

        Assert.Equal("data:image/png;base64,raw-base64", payload.GetProperty("image").GetProperty("url").GetString());
    }

    private static JsonElement BuildPayload(VideoRequest request)
    {
        var method = typeof(XAIProvider).GetMethod("BuildXaiVideoPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(XAIProvider), "BuildXaiVideoPayload");

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

    private static VideoFile Image(string? mediaType, string data)
        => new()
        {
            MediaType = mediaType!,
            Data = data
        };
}
