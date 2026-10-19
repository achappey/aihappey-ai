using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Coze;
using AIHappey.HeaderAuth;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Coze;

public sealed class CozeProviderTests
{
    [Fact]
    public async Task ListModels_discovers_published_bots_from_each_workspace()
    {
        var requests = new List<(Uri Uri, string? Scheme, string? Token)>();
        var provider = CreateProvider("test-key", request =>
        {
            requests.Add((
                request.RequestUri!,
                request.Headers.Authorization?.Scheme,
                request.Headers.Authorization?.Parameter));

            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/workspaces" => JsonResponse(
                    """{"data":{"workspaces":[{"id":"workspace-1","name":"Acme"}],"total_count":1}}"""),
                "/v1/bots" => JsonResponse(
                    """{"data":{"items":[{"id":"bot-1","name":"Helper","description":"Answers questions"}],"total":1}}"""),
                _ => throw new InvalidOperationException($"Unexpected Coze URL: {request.RequestUri}")
            };
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("coze/workspace-1/bot-1", model.Id);
        Assert.Equal("Helper", model.Name);
        Assert.Equal("Answers questions", model.Description);
        Assert.Equal("coze", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Contains("agent", model.Tags!);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("test-key", request.Token);
        });
        Assert.Contains("page_num=1", requests[0].Uri.Query);
        Assert.Contains("workspace_id=workspace-1", requests[1].Uri.Query);
        Assert.Contains("publish_status=published_online", requests[1].Uri.Query);
    }

    [Fact]
    public async Task ListModels_without_api_key_does_not_call_coze()
    {
        var provider = CreateProvider(
            null,
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("Unexpected request.")));

        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task Streaming_maps_text_usage_and_terminal_events()
    {
        var provider = CreateProvider("test-key", _ => SseResponse("""
            event: conversation.chat.created
            data: {"id":"chat-1","created_at":1710000000}

            event: conversation.message.delta
            data: {"id":"message-1","role":"assistant","type":"answer","content_type":"text","content":"Hello"}

            event: conversation.chat.completed
            data: {"id":"chat-1","status":"completed","usage":{"token_count":4,"input_count":2,"output_count":2}}

            event: done
            data:

            """));
        var options = new ChatCompletionOptions
        {
            Model = "coze/workspace-1/bot-1",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hi")
                }
            ]
        };

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(options))
            updates.Add(update);

        Assert.Equal(3, updates.Count);
        var deltaChoice = Assert.IsType<Dictionary<string, object?>>(Assert.Single(updates[0].Choices));
        var delta = Assert.IsType<Dictionary<string, object?>>(deltaChoice["delta"]);
        Assert.Equal("Hello", delta["content"]);
        var usage = Assert.IsType<Dictionary<string, int>>(updates[1].Usage);
        Assert.Equal(4, usage["total_tokens"]);
        var finishChoice = Assert.IsType<Dictionary<string, object?>>(Assert.Single(updates[2].Choices));
        Assert.Equal("stop", finishChoice["finish_reason"]);
    }

    [Fact]
    public async Task Chat_stream_maps_native_text_and_usage_and_passes_native_options()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var provider = CreateProvider("test-key", async request =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return SseResponse("""
                event: conversation.chat.created
                data: {"id":"chat-1","conversation_id":"conversation-1","created_at":1710000000}

                event: conversation.message.delta
                data: {"id":"message-1","role":"assistant","type":"answer","content_type":"text","content":"Hello"}

                event: conversation.chat.completed
                data: {"id":"chat-1","status":"completed","usage":{"token_count":4,"input_count":2,"output_count":2}}

                event: done
                data:

                """);
        });
        var options = new ChatCompletionOptions
        {
            Model = "coze/workspace-1/bot-1",
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement("Hi")
                }
            ],
            AdditionalProperties = new()
            {
                ["conversation_id"] = JsonSerializer.SerializeToElement("conversation-1"),
                ["user_id"] = JsonSerializer.SerializeToElement("user-1"),
                ["parameters"] = JsonSerializer.SerializeToElement(new { tone = "concise" })
            }
        };

        var response = await provider.CompleteChatAsync(options);

        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Headers.Authorization?.Parameter);
        Assert.Equal("/v3/chat", captured.RequestUri!.AbsolutePath);
        Assert.Contains("conversation_id=conversation-1", captured.RequestUri.Query);
        Assert.Equal("text/event-stream", captured.Headers.Accept.Single().MediaType);

        using var requestBody = JsonDocument.Parse(body!);
        var json = requestBody.RootElement;
        Assert.Equal("bot-1", json.GetProperty("bot_id").GetString());
        Assert.Equal("user-1", json.GetProperty("user_id").GetString());
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.Equal("concise", json.GetProperty("parameters").GetProperty("tone").GetString());
        var message = Assert.Single(json.GetProperty("additional_messages").EnumerateArray().ToArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("question", message.GetProperty("type").GetString());
        Assert.Equal("text", message.GetProperty("content_type").GetString());
        Assert.Equal("Hi", message.GetProperty("content").GetString());

        using var result = JsonSerializer.SerializeToDocument(response);
        var choice = Assert.Single(result.RootElement.GetProperty("choices").EnumerateArray().ToArray());
        Assert.Equal("Hello", choice.GetProperty("message").GetProperty("content").GetString());
        Assert.Equal("chat-1", result.RootElement.GetProperty("id").GetString());
        Assert.Equal("coze/workspace-1/bot-1", result.RootElement.GetProperty("model").GetString());
        Assert.Equal(2, result.RootElement.GetProperty("usage").GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(2, result.RootElement.GetProperty("usage").GetProperty("completion_tokens").GetInt32());
        Assert.Equal(4, result.RootElement.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Chat_rejects_system_messages_instead_of_silently_dropping_them()
    {
        var provider = CreateProvider(
            "test-key",
            (Func<HttpRequestMessage, HttpResponseMessage>)(_ => throw new InvalidOperationException("Unexpected request.")));
        var options = new ChatCompletionOptions
        {
            Model = "coze/workspace-1/bot-1",
            Messages =
            [
                new ChatMessage
                {
                    Role = "system",
                    Content = JsonSerializer.SerializeToElement("Follow these instructions.")
                }
            ]
        };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CompleteChatAsync(options));
    }

    [Fact]
    public void Coze_api_key_uses_the_registered_provider_header()
    {
        Assert.Equal("X-Coze-Key", HeaderApiKeyResolver.SupportedProviderHeaders["coze"]);
    }

    private static CozeProvider CreateProvider(
        string? apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(apiKey, request => Task.FromResult(responder(request)));

    private static CozeProvider CreateProvider(
        string? apiKey,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(
            new StaticApiKeyResolver(apiKey),
            new StaticHttpClientFactory(new StaticResponseHandler(responder)),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string content)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "text/event-stream")
        };

    private sealed class StaticApiKeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class StaticHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
