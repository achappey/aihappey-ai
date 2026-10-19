using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.GigaChat;

namespace AIHappey.Tests.GigaChat;

public sealed class GigaChatProviderTests
{
    [Fact]
    public async Task ListModels_discovers_chat_models_and_preserves_native_metadata()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider("encoded-key", request =>
        {
            requests.Add(request);
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
            {
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal("encoded-key", request.Headers.Authorization?.Parameter);
                Assert.Equal("GIGACHAT_API_PERS", ReadFormValue(request, "scope"));
                Assert.True(request.Headers.Contains("RqUID"));
                return JsonResponse("""{"access_token":"access-token","expires_at":4102444800000}""");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/api/v1/models", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("access-token", request.Headers.Authorization?.Parameter);
            return JsonResponse(
                """{"object":"list","data":[{"id":"GigaChat-2","object":"model","owned_by":"salutedevices","type":"chat","context_length":128000},{"id":"Embeddings-2","object":"model","owned_by":"salutedevices","type":"embedder"}]}""");
        });

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("gigachat/GigaChat-2", model.Id);
        Assert.Equal("GigaChat-2", model.Name);
        Assert.Equal("salutedevices", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Equal(128000, model.Metadata!["context_length"].GetInt32());
        Assert.Equal(2, requests.Count);
    }

    [Fact]
    public async Task CompleteChat_forwards_native_options_and_maps_usage()
    {
        HttpRequestMessage? chatRequest = null;
        var provider = CreateProvider("encoded-key", request =>
        {
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
                return JsonResponse("""{"access_token":"access-token"}""");

            chatRequest = request;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("GigaChat-2", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(0.35, body.RootElement.GetProperty("temperature").GetDouble());
            return JsonResponse(
                """{"id":"chatcmpl-1","object":"chat.completion","created":1768996171,"model":"GigaChat-2","choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}""");
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "GigaChat-2",
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
                ["temperature"] = JsonSerializer.SerializeToElement(0.35)
            }
        });

        Assert.Equal("/api/v1/chat/completions", chatRequest!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", chatRequest.Headers.Authorization?.Scheme);
        Assert.Equal("access-token", chatRequest.Headers.Authorization?.Parameter);
        Assert.Equal(5, JsonDocument.Parse(JsonSerializer.Serialize(response.Usage))
            .RootElement.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_maps_text_and_usage()
    {
        var provider = CreateProvider("encoded-key", request =>
        {
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
                return JsonResponse("""{"access_token":"access-token"}""");

            Assert.Equal("text/event-stream", request.Headers.Accept.Single().MediaType);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"Hello\"},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
                       {
                           Model = "GigaChat-2",
                           Messages =
                           [
                               new ChatMessage
                               {
                                   Role = "user",
                                   Content = JsonSerializer.SerializeToElement("Hi")
                               }
                           ]
                       }))
            updates.Add(update);

        Assert.Equal(2, updates.Count);
        Assert.Equal("gigachat/GigaChat-2", updates[0].Model);
        var choice = Assert.IsType<JsonElement>(Assert.Single(updates[0].Choices));
        Assert.Equal("Hello", choice.GetProperty("delta").GetProperty("content").GetString());
        var usage = Assert.IsType<JsonElement>(updates[1].Usage);
        Assert.Equal(5, usage.GetProperty("total_tokens").GetInt32());
    }

    private static GigaChatProvider CreateProvider(
        string? apiKey,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new KeyResolver(apiKey), new ClientFactory(new Handler(responder)));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string? ReadFormValue(HttpRequestMessage request, string name)
    {
        var form = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        return form.Split('&')
            .Select(pair => pair.Split('=', 2))
            .Where(parts => parts.Length == 2)
            .FirstOrDefault(parts => Uri.UnescapeDataString(parts[0]) == name)
            ?.ElementAtOrDefault(1) is { } value
                ? Uri.UnescapeDataString(value)
                : null;
    }

    private sealed class KeyResolver(string? apiKey) : IApiKeyResolver
    {
        public string? Resolve(string provider) => apiKey;
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
