using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.RekaAI;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RekaAI;

public sealed class RekaAIProviderVideoInputTests
{
    [Theory]
    [InlineData("https://cdn.example.test/short.mp4", "https://cdn.example.test/short.mp4")]
    [InlineData("data:video/mp4;base64,AQID", "data:video/mp4;base64,AQID")]
    [InlineData("AQID", "data:video/mp4;base64,AQID")]
    public async Task UnifiedVideoInputUsesRekaVideoUrlPart(string input, string expected)
    {
        var handler = new RecordingHandler(JsonResponse(new
        {
            id = "chatcmpl-reka-video",
            created = 1_788_000_000,
            model = "reka-flash-3",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "done" }, finish_reason = "stop" } }
        }));
        var provider = CreateProvider(handler);

        await provider.ExecuteUnifiedAsync(CreateRequest(input));

        var payload = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        var content = payload.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("video_url", content[0].GetProperty("type").GetString());
        Assert.Equal(expected, content[0].GetProperty("video_url").GetString());
        Assert.False(content[0].TryGetProperty("file", out _));
        Assert.Equal("text", content[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task StreamingUnifiedVideoInputUsesSameRekaVideoUrlPart()
    {
        var handler = new RecordingHandler(SseResponse(
            new
            {
                id = "chatcmpl-reka-video-stream",
                created = 1_788_000_000,
                model = "reka-flash-3",
                choices = new[] { new { index = 0, delta = new { content = "done" }, finish_reason = "stop" } }
            }));
        var provider = CreateProvider(handler);

        await foreach (var _ in provider.StreamUnifiedAsync(CreateRequest("BAUG")))
        {
        }

        var payload = JsonDocument.Parse(Assert.Single(handler.Bodies)).RootElement;
        Assert.True(payload.GetProperty("stream").GetBoolean());
        var video = payload.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("video_url", video.GetProperty("type").GetString());
        Assert.Equal("data:video/mp4;base64,BAUG", video.GetProperty("video_url").GetString());
    }

    private static AIRequest CreateRequest(string videoData)
        => new()
        {
            ProviderId = "rekaai",
            Model = "rekaai/reka-flash-3",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AIFileContentPart
                            {
                                Type = "file",
                                MediaType = "video/mp4",
                                Filename = "short.mp4",
                                Data = videoData
                            },
                            new AITextContentPart { Type = "text", Text = "Describe this video." }
                        ]
                    }
                ]
            }
        };

    private static RekaAIProvider CreateProvider(RecordingHandler handler)
        => new(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new FixedHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.reka.ai/") }));

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(params object[] events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Join("\n\n", events.Select(@event => $"data: {JsonSerializer.Serialize(@event, JsonSerializerOptions.Web)}").Append("data: [DONE]")),
                Encoding.UTF8,
                "text/event-stream")
        };

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queuedResponses = new(responses);

        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "reka-flash-3",
                            name = "Reka Flash 3",
                            description = "Multimodal model",
                            context_length = 128000,
                            max_output_length = 8192
                        }
                    }
                });
            }

            Bodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(queuedResponses.TryDequeue(out var response), $"No response queued for {request.Method} {request.RequestUri}.");
            return response;
        }
    }
}
