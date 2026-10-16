using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Hedra;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Hedra;

public sealed class HedraProviderTests
{
    [Fact]
    public async Task Discovery_preserves_all_modalities_and_metadata_without_filtering()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v3/models", request.RequestUri!.AbsolutePath);
            Assert.Equal("Key", request.Headers.Authorization?.Scheme);
            return Json("""
                {"data":[
                  {"id":"needs-inputs","name":"Video model","modality":"video","price_description":"per second","required_inputs":["audio"]},
                  {"id":"speech","name":"Voice","modality":"audio"},
                  {"id":"chat","name":"Chat model","modality":"text","context_length":1000,"max_output_tokens":200},
                  {"id":"future","name":"Future modality","modality":"other","custom":{"enabled":true}}
                ],"next_cursor":null}
                """);
        });

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(4, models.Length);
        Assert.Equal("hedra/needs-inputs", models[0].Id);
        Assert.Equal("Video model", models[0].Name);
        Assert.Equal("video", models[0].Type);
        Assert.Equal("speech", models[1].Type);
        Assert.Equal("language", models[2].Type);
        Assert.Equal(1000, models[2].ContextWindow);
        Assert.Equal(200, models[2].MaxTokens);
        var serialized = JsonSerializer.SerializeToElement<IEnumerable<Model>>(models);
        Assert.Equal("video", serialized[0].GetProperty("modality").GetString());
        Assert.Equal("per second", serialized[0].GetProperty("price_description").GetString());
        Assert.True(serialized[3].GetProperty("custom").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Discovery_without_a_key_is_empty()
    {
        var provider = Create(_ => throw new InvalidOperationException("Must not call upstream"), null);
        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task Chat_uses_native_bearer_endpoint_and_preserves_tools_reasoning_and_usage()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v3/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            using var payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().Result);
            Assert.Equal("high", payload.RootElement.GetProperty("reasoning_effort").GetString());
            Assert.Single(payload.RootElement.GetProperty("tools").EnumerateArray());
            return Json("""
                {"id":"job_chat","object":"chat.completion","created":1,"model":"chat",
                 "choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"weather","arguments":"{}"}}]},"finish_reason":"tool_calls"}],
                 "usage":{"prompt_tokens":4,"completion_tokens":2,"total_tokens":6}}
                """);
        });

        var result = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "chat",
            ReasoningEffort = "high",
            Tools = [new { type = "function", function = new { name = "weather" } }]
        });
        var json = JsonSerializer.SerializeToElement(result);
        Assert.Equal(6, json.GetProperty("usage").GetProperty("total_tokens").GetInt32());
        Assert.Equal("tool_calls", json.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
    }

    [Fact]
    public async Task Responses_uses_native_endpoint()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v3/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Json("""{"id":"resp_1","object":"response","created_at":1,"model":"chat","status":"completed","output":[],"usage":{"input_tokens":3,"output_tokens":2,"total_tokens":5}}""");
        });
        var result = await provider.ResponsesAsync(JsonSerializer.Deserialize<ResponseRequest>(
            """{"model":"chat","input":"Hello","reasoning":{"effort":"high"}}""")!);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Messages_uses_native_endpoint_and_anthropic_headers()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v3/anthropic/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());
            return Json("""{"id":"msg_1","type":"message","role":"assistant","model":"chat","content":[{"type":"text","text":"Hi"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":1}}""");
        });
        var result = await provider.MessagesAsync(JsonSerializer.Deserialize<MessagesRequest>(
            """{"model":"chat","messages":[{"role":"user","content":"Hello"}],"max_tokens":100}""")!,
            new() { ["anthropic-version"] = "2023-06-01" });
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Chat_stream_preserves_native_events_and_usage()
    {
        var provider = Create(request =>
        {
            Assert.Equal("/v3/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return Sse("""
                data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"chat","choices":[{"index":0,"delta":{"role":"assistant","content":"Hi"},"finish_reason":null}]}

                data: {"id":"chat_1","object":"chat.completion.chunk","created":1,"model":"chat","choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":2}}

                data: [DONE]

                """);
        });
        var events = new List<ChatCompletionUpdate>();
        await foreach (var item in provider.CompleteChatStreamingAsync(new() { Model = "chat", Stream = true }))
            events.Add(item);
        Assert.NotEmpty(events);
        Assert.Contains(events, item => JsonSerializer.Serialize(item).Contains("\"total_tokens\":2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Video_job_is_stateless_and_maps_standard_inputs()
    {
        string? body = null;
        var submitter = Create(request =>
        {
            Assert.Equal("/v3/models/video-model", request.RequestUri!.AbsolutePath);
            Assert.Equal("Key", request.Headers.Authorization?.Scheme);
            body = request.Content!.ReadAsStringAsync().Result;
            return Json("""{"job_id":"job_1","status":"IN_QUEUE"}""", HttpStatusCode.Accepted);
        });
        var started = await submitter.StartVideoOperation(new()
        {
            Model = "video-model", Prompt = "Ocean", Duration = 5, Resolution = "720p",
            Image = new VideoFileUrl { Url = "https://example.com/input.png" },
            ProviderOptions = new() { ["hedra"] = JsonSerializer.SerializeToElement(new { quality = "high", custom = 42 }) }
        });
        using var payload = JsonDocument.Parse(body!);
        var input = payload.RootElement.GetProperty("input");
        Assert.Equal("Ocean", input.GetProperty("prompt").GetString());
        Assert.Equal("720p", input.GetProperty("resolution").GetString());
        Assert.Contains("https://example.com/input.png", body, StringComparison.Ordinal);
        Assert.Contains("42", body, StringComparison.Ordinal);

        var poller = Create(request =>
        {
            if (request.RequestUri!.Host == "cdn.example.com")
            {
                Assert.Null(request.Headers.Authorization);
                return Binary("video/mp4");
            }
            Assert.Equal("/v3/jobs/job_1", request.RequestUri!.AbsolutePath);
            return Json("""{"job_id":"job_1","model":"video-model","status":"COMPLETED","outputs":[{"url":"https://cdn.example.com/video.mp4","content_type":"video/mp4"}]}""");
        });
        var result = Assert.IsType<VideoOperationCompletedResult>(await poller.GetVideoOperationStatus(started.Operation));
        Assert.Equal("hedra/video-model", result.Response.ModelId);
        Assert.NotEmpty(result.Videos);
        Assert.Contains("https://cdn.example.com/video.mp4", JsonSerializer.Serialize(result.ProviderMetadata), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("IN_QUEUE")]
    [InlineData("IN_PROGRESS")]
    public async Task Video_pending_states(string status)
    {
        var provider = Create(_ => Json($$"""{"job_id":"job_1","model":"video","status":"{{status}}","outputs":[]}"""));
        Assert.IsType<VideoOperationPendingResult>(await provider.GetVideoOperationStatus("job_1"));
    }

    [Fact]
    public async Task Video_failure_preserves_upstream_error()
    {
        var provider = Create(_ => Json("""{"job_id":"job_1","model":"video","status":"FAILED","error":{"code":"INVALID_ARGUMENT","message":"unsupported resolution"}}"""));
        var result = Assert.IsType<VideoOperationErrorResult>(await provider.GetVideoOperationStatus("job_1"));
        Assert.Contains("unsupported resolution", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Image_job_download_is_unauthenticated_and_preserves_output_metadata()
    {
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Json("""{"job_id":"job_image","status":"IN_QUEUE"}""", HttpStatusCode.Accepted);
            if (request.RequestUri!.Host == "api.hedra.com")
                return Json("""{"job_id":"job_image","model":"image","status":"COMPLETED","outputs":[{"url":"https://cdn.example.com/image.webp","content_type":"image/webp"}]}""");
            Assert.Null(request.Headers.Authorization);
            return Binary("image/webp");
        });
        var result = await provider.ImageRequest(new() { Model = "image", Prompt = "Cat" });
        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(result.Images!));
        Assert.Contains("https://cdn.example.com/image.webp", JsonSerializer.Serialize(result.ProviderMetadata), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audio_job_uses_generic_model_execution()
    {
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/v3/models/voice", request.RequestUri!.AbsolutePath);
                Assert.Equal("Key", request.Headers.Authorization?.Scheme);
                Assert.Contains("Hello", request.Content!.ReadAsStringAsync().Result, StringComparison.Ordinal);
                return Json("""{"job_id":"job_audio","status":"IN_QUEUE"}""", HttpStatusCode.Accepted);
            }
            if (request.RequestUri!.Host == "api.hedra.com")
                return Json("""{"job_id":"job_audio","model":"voice","status":"COMPLETED","outputs":[{"url":"https://cdn.example.com/audio.mp3","content_type":"audio/mpeg"}]}""");
            Assert.Null(request.Headers.Authorization);
            return Binary("audio/mpeg");
        });
        var result = await provider.SpeechRequest(new() { Model = "voice", Text = "Hello" });
        Assert.Equal("AQID", result.Audio.Base64);
        Assert.Equal("audio/mpeg", result.Audio.MimeType);
    }

    [Fact]
    public async Task Unsupported_parameter_errors_are_not_replaced_or_retried()
    {
        var calls = 0;
        var provider = Create(_ =>
        {
            calls++;
            return Json("""{"error":{"code":"INVALID_ARGUMENT","message":"quality is unsupported"}}""", HttpStatusCode.UnprocessableEntity);
        });
        var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.ImageRequest(new() { Model = "image", Prompt = "Cat" }));
        Assert.Contains("quality is unsupported", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Media_polling_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Create(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Json("""{"job_id":"job_image","status":"IN_QUEUE"}""");
            cancellation.Cancel();
            return Json("""{"job_id":"job_image","status":"IN_PROGRESS","outputs":[]}""");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.ImageRequest(new() { Model = "image", Prompt = "Cat" }, cancellation.Token));
    }

    private static HedraProvider Create(Func<HttpRequestMessage, HttpResponseMessage> handler, string? key = "test-key")
        => new(new Keys(key), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())), new Clients(handler));

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Sse(string data)
        => new(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "text/event-stream") };

    private static HttpResponseMessage Binary(string mediaType)
    {
        var content = new ByteArrayContent([1, 2, 3]);
        content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return new(HttpStatusCode.OK) { Content = content };
    }

    private sealed class Keys(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }

    private sealed class Clients(Func<HttpRequestMessage, HttpResponseMessage> handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(handler));
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
