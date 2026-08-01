using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.SpaceXAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIVideoPayloadTests
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

    [Fact]
    public void BuildXaiVideoPayloadPassesThroughAllRawXaiFields()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video-1.5",
            Prompt = "<AUDIO_0> narrates the scene",
            ProviderOptions = ProviderOptions("""
                {
                  "reference_audios": [{ "voice_id": "eve" }],
                  "future_option": { "enabled": true }
                }
                """)
        });

        Assert.Equal("eve", payload.GetProperty("reference_audios")[0].GetProperty("voice_id").GetString());
        Assert.True(payload.GetProperty("future_option").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public void BuildXaiVideoPayloadStandardFieldsOverrideRawProviderFields()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video-1.5",
            Prompt = "standard prompt",
            Duration = 12,
            Resolution = "1080p",
            AspectRatio = "9:16",
            Image = Image("image/png", "standard-image"),
            InputReferences = [Image("image/jpeg", "standard-reference")],
            ProviderOptions = ProviderOptions("""
                {
                  "model": "wrong-model",
                  "prompt": "wrong prompt",
                  "duration": 3,
                  "resolution": "480p",
                  "aspect_ratio": "1:1",
                  "image": { "file_id": "wrong-image" },
                  "reference_images": [{ "file_id": "wrong-reference" }]
                }
                """)
        });

        Assert.Equal("grok-imagine-video-1.5", payload.GetProperty("model").GetString());
        Assert.Equal("standard prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(12, payload.GetProperty("duration").GetInt32());
        Assert.Equal("1080p", payload.GetProperty("resolution").GetString());
        Assert.Equal("9:16", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("data:image/png;base64,standard-image", payload.GetProperty("image").GetProperty("url").GetString());
        Assert.Equal("data:image/jpeg;base64,standard-reference", payload.GetProperty("reference_images")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void BuildXaiVideoPayloadPreservesRawFileIdMediaObjects()
    {
        var payload = BuildPayload(new VideoRequest
        {
            Model = "grok-imagine-video-1.5",
            Prompt = "use Files API inputs",
            ProviderOptions = ProviderOptions("""
                {
                  "image": { "file_id": "file-start" },
                  "reference_images": [{ "file_id": "file-reference" }]
                }
                """)
        });

        Assert.Equal("file-start", payload.GetProperty("image").GetProperty("file_id").GetString());
        Assert.Equal("file-reference", payload.GetProperty("reference_images")[0].GetProperty("file_id").GetString());
    }

    private static JsonElement BuildPayload(VideoRequest request)
    {
        var method = typeof(SpaceXAIProvider).GetMethod("BuildXaiVideoPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(SpaceXAIProvider), "BuildXaiVideoPayload");

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

    private static Dictionary<string, JsonElement> ProviderOptions(string json)
        => new()
        {
            ["spacexai"] = JsonDocument.Parse(json).RootElement.Clone()
        };
}
