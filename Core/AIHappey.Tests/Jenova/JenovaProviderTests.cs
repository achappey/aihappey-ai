using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Jenova;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Jenova;

public sealed class JenovaProviderTests
{
    [Fact]
    public async Task Discovery_lists_synthetic_agent_and_model_slugs()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            return request.RequestUri?.AbsolutePath switch
            {
                "/v1/agents" => JsonResponse(new
                {
                    agents = new[] { new { agent = "support", display_name = "Support", description = "Help desk" } }
                }),
                "/v1/models" => JsonResponse(new
                {
                    models = new[] { new { id = "claude-sonnet-5", name = "Claude Sonnet 5", thinking_variant = "claude-sonnet-5-thinking" } }
                }),
                _ => throw new InvalidOperationException($"Unexpected request: {request.RequestUri}")
            };
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Contains(models, model => model.Id == "jenova/agent/support" && model.Tags!.Contains("agent"));
        Assert.Contains(models, model => model.Id == "jenova/model/claude-sonnet-5" && model.Tags!.Contains("reasoning"));
    }

    [Fact]
    public async Task New_message_serializes_content_file_urls_options_and_idempotency()
    {
        JsonElement sent = default;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/messages", request.RequestUri?.AbsolutePath);
            Assert.Equal("send-1", Assert.Single(request.Headers.GetValues("Idempotency-Key")));
            sent = Body(request);
            return JsonResponse(new
            {
                id = "msg-1", session_id = "ses-1", run_id = "run-1", content = "Done",
                model = "claude-sonnet-5", stop_reason = "end_run",
                files = new[] { new { file_id = "f1", name = "report.pdf", url = "https://cdn.example/report.pdf", format = "pdf", size = 9 } },
                usage = new { cost = "0.0032" }
            });
        });

        var response = await provider.ExecuteUnifiedAsync(Request("jenova/agent/support",
            "Summarize this", new { model = "claude-sonnet-5", user = "user-1", session_name = "Review",
                idempotency_key = "send-1", future_option = new { enabled = true },
                file_urls = new[] { "https://files.example/input.pdf" } }));

        Assert.Equal("support", sent.GetProperty("agent").GetString());
        Assert.Equal("Summarize this", sent.GetProperty("content").GetString());
        Assert.Equal("claude-sonnet-5", sent.GetProperty("model").GetString());
        Assert.Equal("true", sent.GetProperty("future_option").GetProperty("enabled").GetRawText());
        Assert.Equal("https://files.example/input.pdf", sent.GetProperty("file_urls")[0].GetString());
        Assert.Equal("false", sent.GetProperty("stream").GetRawText());
        Assert.Equal("ses-1", MetadataValue(response, "session_id"));
        Assert.Contains(Assert.Single(response.Output!.Items!).Content!, part => part is AIFileContentPart);
    }

    [Fact]
    public async Task Existing_session_posts_to_session_endpoint_without_local_session_state()
    {
        JsonElement sent = default;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/sessions/ses-existing/messages", request.RequestUri?.AbsolutePath);
            sent = Body(request);
            return JsonResponse(new { id = "msg-2", session_id = "ses-existing", content = "Continued", stop_reason = "end_run" });
        });

        var response = await provider.ExecuteUnifiedAsync(Request("jenova/agent/support", "Continue",
            new { session_id = "ses-existing", user = "user-1", model = "claude-sonnet-5" }));

        Assert.False(sent.TryGetProperty("session_id", out _));
        Assert.Equal("ses-existing", MetadataValue(response, "session_id"));
        Assert.Equal("Continued", Assert.IsType<AITextContentPart>(Assert.Single(response.Output!.Items!).Content![0]).Text);
    }

    [Fact]
    public async Task Streaming_maps_deltas_thinking_progress_mcp_files_and_usage()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(new
                {
                    id = "msg-1", session_id = "ses-1", content = "Answer",
                    files = new[] { new { file_id = "file-1", name = "output.pdf", url = "https://cdn.example/output.pdf", format = "pdf" } }
                });

            var events = """
                event: stream_started
                data: {"session_id":"ses-1","run_id":"run-1","agent":"support"}

                event: stream_delta
                data: {"session_id":"ses-1","run_id":"run-1","chunk_content":"Hello","seq":1}

                event: stream_thinking
                data: {"session_id":"ses-1","run_id":"run-1","content":"Checking"}

                event: stream_progress
                data: {"session_id":"ses-1","run_id":"run-1","state":"running","label":"Searching"}

                event: mcp_connection
                data: {"session_id":"ses-1","run_id":"run-1","connection_server_list":[{"mcp_server_id":"server-1","auth_url":"https://auth.example"}]}

                event: message_completed
                data: {"session_id":"ses-1","run_id":"run-1","message_id":"msg-1","type":"external"}

                event: stream_ended
                data: {"session_id":"ses-1","run_id":"run-1","success":true,"stop_reason":"end_run","usage":{"cost":"0.0032"}}

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
            };
        });

        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(Request("jenova/agent/support", "Hello")))
            events.Add(item);

        Assert.Contains(events, item => item.Event.Type == "text-delta"
            && Assert.IsType<AITextDeltaEventData>(item.Event.Data).Delta == "Hello");
        Assert.Contains(events, item => item.Event.Type == "reasoning-delta");
        Assert.Contains(events, item => item.Event.Type == "data-jenova-stream_progress");
        Assert.Contains(events, item => item.Event.Type == "data-jenova-mcp_connection"
            && JsonSerializer.Serialize(item.Event.Data).Contains("auth_url", StringComparison.Ordinal));
        Assert.Contains(events, item => item.Event.Type == "file"
            && Assert.IsType<AIFileEventData>(item.Event.Data).Url == "https://cdn.example/output.pdf");
        Assert.Contains(events, item => item.Event.Type == "finish"
            && Assert.IsType<AIFinishEventData>(item.Event.Data).FinishReason == "end_run");
    }

    [Fact]
    public async Task Http_errors_keep_status_and_error_code_message()
    {
        var provider = CreateProvider(_ => JsonResponse(new
        {
            error = new { code = "invalid_model_selection", message = "Model unavailable" }
        }, HttpStatusCode.BadRequest));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.ExecuteUnifiedAsync(Request("jenova/agent/support", "Hello")));

        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("Model unavailable", error.Message);
    }

    [Fact]
    public async Task Accepted_non_streaming_run_preserves_async_status_and_identifiers()
    {
        var provider = CreateProvider(_ => JsonResponse(new
        {
            status = "processing", session_id = "ses-async", run_id = "run-async",
            message = "The run continues in the background."
        }, HttpStatusCode.Accepted));

        var response = await provider.ExecuteUnifiedAsync(Request("jenova/agent/support", "Run"));

        Assert.Equal("processing", response.Status);
        Assert.Equal("ses-async", MetadataValue(response, "session_id"));
        Assert.Equal("run-async", MetadataValue(response, "run_id"));
    }

    private static string? MetadataValue(AIResponse response, string name)
    {
        var metadata = Assert.IsType<Dictionary<string, object?>>(response.Metadata!["jenova"]);
        return metadata.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    private static AIRequest Request(string model, string content, object? metadata = null)
        => new()
        {
            ProviderId = "jenova",
            Model = model,
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "user",
                    Content = [new AITextContentPart { Type = "text", Text = content }]
                }]
            },
            Metadata = metadata is null
                ? null
                : new Dictionary<string, object?> { ["jenova"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web) }
        };

    private static JsonElement Body(HttpRequestMessage request)
        => JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(object body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static JenovaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
