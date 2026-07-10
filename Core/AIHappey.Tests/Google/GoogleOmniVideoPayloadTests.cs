using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Google;

public sealed class GoogleOmniVideoPayloadTests
{
    [Fact]
    public void BuildOmniVideoPayloadUsesSynchronousVideoDefaultsAndHighReasoning()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "google/gemini-omni-flash-preview",
            Prompt = "A beautiful sunset over a calm ocean.",
            AspectRatio = "9:16"
        });

        Assert.Equal("gemini-omni-flash-preview", payload.GetProperty("model").GetString());
        Assert.Equal("A beautiful sunset over a calm ocean.", payload.GetProperty("input").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.False(payload.GetProperty("background").GetBoolean());
        Assert.False(payload.GetProperty("store").GetBoolean());
        Assert.Equal("video", payload.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("9:16", payload.GetProperty("response_format").GetProperty("aspect_ratio").GetString());
        Assert.Equal("high", payload.GetProperty("generation_config").GetProperty("thinking_level").GetString());
        Assert.Equal("text_to_video", payload.GetProperty("generation_config").GetProperty("video_config").GetProperty("task").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildOmniVideoPayloadMapsImageToVideoTaskAndInputContent()
    {
        var (payload, _) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = "Turn this into realistic footage.",
            Image = new VideoFile
            {
                MediaType = "image/jpeg",
                Data = "image-base64"
            }
        });

        var input = payload.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal("image", input[0].GetProperty("type").GetString());
        Assert.Equal("image-base64", input[0].GetProperty("data").GetString());
        Assert.Equal("image/jpeg", input[0].GetProperty("mime_type").GetString());
        Assert.Equal("text", input[^1].GetProperty("type").GetString());
        Assert.Equal("image_to_video", payload.GetProperty("generation_config").GetProperty("video_config").GetProperty("task").GetString());
    }

    [Fact]
    public void BuildOmniVideoPayloadMapsReferenceInputsToReferenceTask()
    {
        var (payload, _) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = "A cat playfully batting at yarn.",
            InputReferences =
            [
                new VideoFile { MediaType = "image/png", Data = "cat" },
                new VideoFile { MediaType = "image/png", Data = "yarn" }
            ]
        });

        Assert.Equal("reference_to_video", payload.GetProperty("generation_config").GetProperty("video_config").GetProperty("task").GetString());
        Assert.Equal(3, payload.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void BuildOmniVideoPayloadAllowsExplicitTaskAndUriDelivery()
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = "Make the lighting more dramatic. Keep everything else the same.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    previous_interaction_id = "v1_previous",
                    delivery = "uri",
                    task = "edit",
                    stream = true,
                    generation_config = new
                    {
                        thinking_level = "low",
                        video_config = new { task = "text_to_video" }
                    }
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.Equal("v1_previous", payload.GetProperty("previous_interaction_id").GetString());
        Assert.Equal("uri", payload.GetProperty("response_format").GetProperty("delivery").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.Equal("high", payload.GetProperty("generation_config").GetProperty("thinking_level").GetString());
        Assert.Equal("edit", payload.GetProperty("generation_config").GetProperty("video_config").GetProperty("task").GetString());
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("stream", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryExtractGoogleOmniVideoReadsRestStepsVideoOutput()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            steps = new object[]
            {
                new
                {
                    type = "model_output",
                    content = new object[]
                    {
                        new
                        {
                            type = "video",
                            mime_type = "video/mp4",
                            data = "video-base64"
                        }
                    }
                }
            }
        }, JsonSerializerOptions.Web);

        var (found, base64, uri, mimeType) = ExtractVideo(json);

        Assert.True(found);
        Assert.Equal("video-base64", base64);
        Assert.Null(uri);
        Assert.Equal("video/mp4", mimeType);
    }

    [Fact]
    public void TryExtractGoogleOmniVideoReadsUriVideoOutput()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            output = new
            {
                type = "video",
                mime_type = "video/mp4",
                uri = "https://generativelanguage.googleapis.com/v1beta/files/abc:download?alt=media"
            }
        }, JsonSerializerOptions.Web);

        var (found, base64, uri, mimeType) = ExtractVideo(json);

        Assert.True(found);
        Assert.Null(base64);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/abc:download?alt=media", uri);
        Assert.Equal("video/mp4", mimeType);
    }

    private static (JsonElement Payload, List<object> Warnings) BuildPayload(VideoRequest request)
    {
        var warnings = new List<object>();
        var method = typeof(GoogleAIProvider).GetMethod("BuildOmniVideoPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "BuildOmniVideoPayload");

        try
        {
            var payload = (JsonObject)method.Invoke(null, [request, warnings])!;
            return (JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), warnings);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static (bool Found, string? Base64, string? Uri, string? MimeType) ExtractVideo(JsonElement root)
    {
        var method = typeof(GoogleAIProvider).GetMethod("TryExtractGoogleOmniVideo", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "TryExtractGoogleOmniVideo");

        var parameters = new object?[] { root, null, null, null };
        var found = (bool)method.Invoke(null, parameters)!;
        return (found, parameters[1] as string, parameters[2] as string, parameters[3] as string);
    }
}
