using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleOmniVideoOperationTests
{
    [Theory]
    [InlineData("gemini-omni-flash-preview")]
    [InlineData("gemini-omni-1.1-flash")]
    public async Task StartVideoOperationRequestsOnlyVideoWithoutRewritingDutchPrompt(string model)
    {
        const string prompt = "simpele video van een kat en een bal die aan het spelen is";
        var calls = 0;
        var provider = CreateProvider(new RecordingHandler(async request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1beta/interactions", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var payload = document.RootElement;
            Assert.Equal(prompt, payload.GetProperty("input").GetString());
            Assert.Equal(model, payload.GetProperty("model").GetString());
            Assert.False(payload.TryGetProperty("system_instruction", out _));
            Assert.False(payload.TryGetProperty("systemInstruction", out _));
            Assert.False(payload.TryGetProperty("generation_config", out _));
            Assert.False(payload.TryGetProperty("generationConfig", out _));
            Assert.False(payload.TryGetProperty("video_config", out _));
            Assert.False(payload.TryGetProperty("task", out _));
            Assert.Equal("video", payload.GetProperty("response_format").GetProperty("type").GetString());
            Assert.Equal("inline", payload.GetProperty("response_format").GetProperty("delivery").GetString());
            Assert.False(payload.GetProperty("stream").GetBoolean());
            Assert.True(payload.GetProperty("background").GetBoolean());
            Assert.True(payload.GetProperty("store").GetBoolean());
            return JsonResponse("""{ "id": "v1_test", "status": "in_progress" }""");
        }));

        var result = await provider.StartVideoOperation(new VideoRequest
        {
            Model = $"google/{model}",
            Prompt = prompt
        });

        Assert.Equal("v1_test", result.Operation);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletedTextOnlyInteractionExplainsFailureWithoutRetryingOrLeakingOtherSteps()
    {
        var calls = 0;
        var provider = CreateProvider(new RecordingHandler(request =>
        {
            calls++;
            AssertPollRequest(request);
            return Task.FromResult(JsonResponse("""
                {
                  "id": "v1_test", "status": "completed", "model": "gemini-omni-flash-preview",
                  "response_format": { "type": "video" },
                  "steps": [
                    { "type": "user_input", "content": [{ "type": "text", "text": "private-input-marker" }] },
                    { "type": "model_output", "content": [{ "type": "text", "text": "Hier is een gedetailleerde prompt voor een videogeneratie-model." }] },
                    { "type": "thought", "signature": "private-thought-marker" }
                  ],
                  "usage": { "total_tokens": 1740 }
                }
                """));
        }));

        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("v1_test"));

        Assert.Contains("text instead of a generated video", result.Error);
        Assert.Contains("Hier is een gedetailleerde prompt", result.Error);
        Assert.DoesNotContain("private-input-marker", result.Error);
        Assert.DoesNotContain("private-thought-marker", result.Error);
        Assert.Equal("google/gemini-omni-flash-preview", result.Response.ModelId);
        Assert.Equal(1740, result.ProviderMetadata!["google"].GetProperty("interaction").GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("cancelled")]
    [InlineData("incomplete")]
    [InlineData("completed")]
    public async Task TerminalFailuresIncludeDocumentedErrorsAndBoundedModelText(string status)
    {
        var json = JsonSerializer.Serialize(new
        {
            status,
            errors = new[] { new { code = "generation_failed", message = "Video could not be generated." } },
            steps = new[]
            {
                new { type = "model_output", content = new[] { new { type = "text", text = new string('x', 2000) + "truncated-marker" } } }
            }
        });
        var provider = CreateProvider(new RecordingHandler(_ => Task.FromResult(JsonResponse(json))));

        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("v1_test"));

        Assert.Contains("generation_failed", result.Error);
        Assert.Contains("Video could not be generated.", result.Error);
        Assert.DoesNotContain("truncated-marker", result.Error);
        Assert.Contains("…", result.Error);
        Assert.True(result.Error.Length < 1000);
    }

    [Fact]
    public async Task LegacyErrorMessageRemainsVisible()
    {
        var provider = CreateProvider(new RecordingHandler(_ => Task.FromResult(JsonResponse("""
            { "status": "failed", "error": { "code": "invalid_request", "message": "Video generation unavailable." } }
            """))));

        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("v1_test"));

        Assert.Contains("invalid_request", result.Error);
        Assert.Contains("Video generation unavailable.", result.Error);
    }

    [Fact]
    public async Task CompletedInteractionWithOnlyInputVideoIsAnErrorNotAnUneditedSuccess()
    {
        var provider = CreateProvider(new RecordingHandler(_ => Task.FromResult(JsonResponse("""
            {
              "status": "completed",
              "steps": [
                { "type": "user_input", "content": [{ "type": "video", "data": "input-video" }] },
                { "type": "model_output", "content": [{ "type": "text", "text": "I cannot edit this video." }] }
              ]
            }
            """))));

        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("v1_test"));

        Assert.Contains("I cannot edit this video.", result.Error);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"steps\": []}")]
    [InlineData("{\"steps\": [{\"type\": \"model_output\", \"content\": [{\"type\": \"video\"}]}]}")]
    public async Task CompletedInteractionWithoutUsableMediaReportsMissingVideo(string fields)
    {
        var root = JsonSerializer.Deserialize<Dictionary<string, object>>(fields)!;
        root["status"] = "completed";
        var provider = CreateProvider(new RecordingHandler(_ => Task.FromResult(JsonResponse(JsonSerializer.Serialize(root)))));

        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("v1_test"));

        Assert.Contains("no video output", result.Error);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("in_progress")]
    public async Task NonTerminalInteractionsRemainPending(string status)
    {
        var provider = CreateProvider(new RecordingHandler(_ => Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { status })))));

        var result = await provider.GetVideoOperationStatus("v1_test");

        Assert.IsType<VideoOperationPendingResult>(result);
        Assert.Equal(status, result.ProviderMetadata!["google"].GetProperty("status").GetString());
    }

    [Theory]
    [InlineData("video/mp4")]
    [InlineData(null)]
    public async Task CompletedInlineVideoPreservesInteractionAndUsageMetadata(string? mimeType)
    {
        var calls = 0;
        var json = JsonSerializer.Serialize(new
        {
            id = "v1_test", status = "completed", model = "gemini-omni-flash-preview",
            usage = new { total_tokens = 1740 },
            steps = new[]
            {
                new { type = "model_output", content = new[] { new { type = "video", data = "Z2VuZXJhdGVk", mime_type = mimeType } } }
            }
        });
        var provider = CreateProvider(new RecordingHandler(request =>
        {
            calls++;
            AssertPollRequest(request);
            return Task.FromResult(JsonResponse(json));
        }));

        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("v1_test"));

        var video = Assert.Single(result.Videos);
        Assert.Equal("base64", video.Type);
        Assert.Equal("Z2VuZXJhdGVk", video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        var metadata = result.ProviderMetadata!["google"];
        Assert.Equal("v1_test", metadata.GetProperty("interactionId").GetString());
        Assert.Equal("completed", metadata.GetProperty("status").GetString());
        Assert.Equal(1740, metadata.GetProperty("interaction").GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CompletedUriVideoWaitsForActiveFileAndDownloadsIt()
    {
        var paths = new List<string>();
        var provider = CreateProvider(new RecordingHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            paths.Add(path);
            var response = path switch
            {
                "/v1beta/interactions/v1_test" => JsonResponse("""
                    {
                      "status": "completed",
                      "steps": [{ "type": "model_output", "content": [{
                        "type": "video", "mime_type": "video/mp4",
                        "uri": "https://generativelanguage.googleapis.com/v1beta/files/generated:download?alt=media"
                      }] }]
                    }
                    """),
                "/v1beta/files/generated" => JsonResponse("""{ "state": "ACTIVE" }"""),
                "/v1beta/files/generated:download?alt=media" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("generated-video-bytes", Encoding.UTF8, "video/mp4")
                },
                _ => throw new InvalidOperationException($"Unexpected request: {path}")
            };
            return Task.FromResult(response);
        }));

        var result = Assert.IsType<VideoOperationCompletedResult>(await provider.GetVideoOperationStatus("v1_test"));

        var video = Assert.Single(result.Videos);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("generated-video-bytes")), video.Data);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal(3, paths.Count);
    }

    private static void AssertPollRequest(HttpRequestMessage request)
    {
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1beta/interactions/v1_test", request.RequestUri!.AbsolutePath);
    }

    private static GoogleAIProvider CreateProvider(HttpMessageHandler handler)
        => new(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(new HttpClient(handler)));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
