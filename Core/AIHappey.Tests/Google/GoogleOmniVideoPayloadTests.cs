using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Google;

public sealed class GoogleOmniVideoPayloadTests
{
    [Theory]
    [InlineData("gemini-omni-flash-preview")]
    [InlineData("gemini-omni-1.1-flash")]
    public void BuildOmniVideoPayloadRequestsOnlyVideoWithoutReasoningOrTask(string model)
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = $"google/{model}",
            Prompt = "A beautiful sunset over a calm ocean.",
            AspectRatio = "9:16"
        });

        Assert.Equal(model, payload.GetProperty("model").GetString());
        Assert.Equal("A beautiful sunset over a calm ocean.", payload.GetProperty("input").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.True(payload.GetProperty("background").GetBoolean());
        Assert.True(payload.GetProperty("store").GetBoolean());
        Assert.Equal("video", payload.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("inline", payload.GetProperty("response_format").GetProperty("delivery").GetString());
        Assert.Equal("9:16", payload.GetProperty("response_format").GetProperty("aspect_ratio").GetString());
        Assert.False(payload.TryGetProperty("system_instruction", out _));
        Assert.False(payload.TryGetProperty("systemInstruction", out _));
        Assert.False(payload.TryGetProperty("generation_config", out _));
        Assert.False(payload.TryGetProperty("video_config", out _));
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildOmniVideoPayloadPreservesImageInputWithoutTaskConfiguration()
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
        Assert.Equal("Turn this into realistic footage.", input[^1].GetProperty("text").GetString());
        Assert.False(payload.TryGetProperty("system_instruction", out _));
        Assert.False(payload.TryGetProperty("generation_config", out _));
    }

    [Fact]
    public void BuildOmniVideoPayloadPreservesReferencesWithoutTaskConfiguration()
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

        Assert.False(payload.TryGetProperty("generation_config", out _));
        Assert.Equal(3, payload.GetProperty("input").GetArrayLength());
    }

    [Fact]
    public void BuildOmniVideoPayloadIgnoresExplicitTaskAndReasoningButPreservesUriDelivery()
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
                    background = false,
                    store = false,
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
        Assert.True(payload.GetProperty("background").GetBoolean());
        Assert.True(payload.GetProperty("store").GetBoolean());
        Assert.False(payload.TryGetProperty("generation_config", out _));
        Assert.False(payload.TryGetProperty("task", out _));
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("stream", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("background", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("store", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("system_instruction")]
    [InlineData("systemInstruction")]
    public void BuildOmniVideoPayloadIgnoresUnsupportedInstructionsWithoutRewritingDutchPrompt(string instructionKey)
    {
        const string prompt = "simpele video van een kat en een bal die aan het spelen is";
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = prompt,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>
                {
                    [instructionKey] = "Use warm lighting."
                })
            }
        });

        var input = payload.GetProperty("input").GetString();
        Assert.Equal(prompt, input);
        Assert.False(payload.TryGetProperty("system_instruction", out _));
        Assert.False(payload.TryGetProperty("systemInstruction", out _));
        var warning = JsonSerializer.SerializeToElement(Assert.Single(warnings));
        Assert.Equal("ignored", warning.GetProperty("type").GetString());
        Assert.Contains("without reasoning", warning.GetProperty("details").GetString());
    }

    [Fact]
    public void BuildOmniVideoPayloadKeepsFirstFrameBeforeTextWithoutInjectingInstructions()
    {
        var (payload, _) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = "Animate the cat.",
            FrameImages =
            [
                new VideoFrameImage
                {
                    FrameType = "first_frame",
                    Image = new VideoFile { MediaType = "image/png", Data = "first-frame" }
                }
            ],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new { system_instruction = "Use warm lighting." })
            }
        });

        var input = payload.GetProperty("input").EnumerateArray().ToList();
        Assert.Equal(2, input.Count);
        Assert.Equal("first-frame", input[0].GetProperty("data").GetString());
        var text = input[1].GetProperty("text").GetString();
        Assert.Contains("<FIRST_FRAME> Animate the cat.", text);
        Assert.DoesNotContain("Generate the actual video", text);
        Assert.DoesNotContain("Use warm lighting.", text);
        Assert.False(payload.TryGetProperty("system_instruction", out _));
        Assert.False(payload.TryGetProperty("generation_config", out _));
    }

    [Theory]
    [InlineData("response_format")]
    [InlineData("responseFormat")]
    public void BuildOmniVideoPayloadPreservesNestedDeliveryOverride(string formatKey)
    {
        var (payload, _) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-flash-preview",
            Prompt = "A playful cat.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    [formatKey] = new { delivery = "uri", aspect_ratio = "16:9" },
                    ["delivery"] = "inline"
                })
            }
        });

        Assert.Equal("uri", payload.GetProperty("response_format").GetProperty("delivery").GetString());
        Assert.Equal("16:9", payload.GetProperty("response_format").GetProperty("aspect_ratio").GetString());
        Assert.False(payload.TryGetProperty("responseFormat", out _));
    }

    [Theory]
    [InlineData("generation_config")]
    [InlineData("generationConfig")]
    [InlineData("video_config")]
    [InlineData("videoConfig")]
    [InlineData("thinking_level")]
    [InlineData("thinkingLevel")]
    [InlineData("thinking_summaries")]
    [InlineData("thinkingSummaries")]
    [InlineData("task")]
    public void BuildOmniVideoPayloadCannotReenableReasoningOrTaskThroughProviderOptions(string option)
    {
        var (payload, warnings) = BuildPayload(new VideoRequest
        {
            Model = "gemini-omni-1.1-flash",
            Prompt = "A cinematic drone shot over Amsterdam at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new Dictionary<string, object>
                {
                    [option] = option.Contains("thinking") ? "high" :
                        option == "task" ? "text_to_video" :
                        new { thinking_level = "high", task = "text_to_video", video_config = new { task = "text_to_video" } }
                })
            }
        });

        Assert.False(payload.TryGetProperty(option, out _));
        Assert.False(payload.TryGetProperty("generation_config", out _));
        Assert.Equal("video", payload.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Single(warnings);
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
            steps = new[]
            {
                new
                {
                    type = "model_output",
                    content = new[]
                    {
                        new
                        {
                            type = "video",
                            mime_type = "video/mp4",
                            uri = "https://generativelanguage.googleapis.com/v1beta/files/abc:download?alt=media"
                        }
                    }
                }
            }
        }, JsonSerializerOptions.Web);

        var (found, base64, uri, mimeType) = ExtractVideo(json);

        Assert.True(found);
        Assert.Null(base64);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/files/abc:download?alt=media", uri);
        Assert.Equal("video/mp4", mimeType);
    }

    [Theory]
    [InlineData("user_input")]
    [InlineData("thought")]
    [InlineData("processing_result")]
    public void TryExtractGoogleOmniVideoDoesNotReturnInputOrOtherStepMedia(string stepType)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            steps = new[]
            {
                new { type = stepType, content = new[] { new { type = "video", data = "input-video" } } }
            },
            response_format = new { type = "video", uri = "https://example.com/not-output.mp4" }
        });

        var (found, base64, uri, mimeType) = ExtractVideo(json);

        Assert.False(found);
        Assert.Null(base64);
        Assert.Null(uri);
        Assert.Null(mimeType);
    }

    [Fact]
    public void TryExtractGoogleOmniVideoChoosesGeneratedVideoAfterInputAndText()
    {
        var json = JsonSerializer.Deserialize<JsonElement>("""
            {
              "steps": [
                { "type": "user_input", "content": [{ "type": "video", "data": "input-video" }] },
                { "type": "model_output", "content": [{ "type": "text", "text": "Here is your video." }] },
                { "type": "model_output", "content": [{ "type": "video", "data": "generated-video" }] }
              ]
            }
            """);

        var (found, base64, _, _) = ExtractVideo(json);

        Assert.True(found);
        Assert.Equal("generated-video", base64);
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
