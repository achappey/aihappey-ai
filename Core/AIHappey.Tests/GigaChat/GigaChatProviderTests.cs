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
    public async Task ListModels_discovers_chat_models_and_maps_native_fields()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider("encoded-key", request =>
        {
            requests.Add(request);
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
            {
                Assert.Equal("/api/v2/oauth", request.RequestUri.AbsolutePath);
                Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
                Assert.Equal("encoded-key", request.Headers.Authorization?.Parameter);
                Assert.Equal("GIGACHAT_API_PERS", ReadFormValue(request, "scope"));
                Assert.True(Guid.TryParse(request.Headers.GetValues("RqUID").Single(), out _));
                return JsonResponse("""{"access_token":"access-token","expires_at":4102444800000}""");
            }

            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.giga.chat", request.RequestUri.Host);
            Assert.Equal("/v1/models", request.RequestUri.AbsolutePath);
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

        Assert.Equal("api.giga.chat", chatRequest!.RequestUri!.Host);
        Assert.Equal("/v1/chat/completions", chatRequest.RequestUri.AbsolutePath);
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

            Assert.Equal("api.giga.chat", request.RequestUri.Host);
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
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

    [Fact]
    public async Task CompleteChat_maps_function_tools_and_calls_to_v1_contract()
    {
        HttpRequestMessage? chatRequest = null;
        var provider = CreateProvider("encoded-key", request =>
        {
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
                return JsonResponse("""{"access_token":"access-token"}""");

            chatRequest = request;
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            var payload = body.RootElement;
            var function = Assert.Single(payload.GetProperty("functions").EnumerateArray());
            Assert.Equal("get_weather", function.GetProperty("name").GetString());
            Assert.False(payload.TryGetProperty("tools", out _));
            Assert.Equal("auto", payload.GetProperty("function_call").GetString());

            var messages = payload.GetProperty("messages");
            var assistant = messages[1];
            Assert.Equal("assistant", assistant.GetProperty("role").GetString());
            Assert.Equal("get_weather", assistant.GetProperty("function_call").GetProperty("name").GetString());
            Assert.Equal("Moscow", assistant.GetProperty("function_call").GetProperty("arguments").GetProperty("city").GetString());
            var functionResult = messages[2];
            Assert.Equal("function", functionResult.GetProperty("role").GetString());
            Assert.Equal("get_weather", functionResult.GetProperty("name").GetString());
            Assert.False(functionResult.TryGetProperty("tool_call_id", out _));

            return JsonResponse(
                """{"id":"chatcmpl-2","object":"chat.completion","created":1768996171,"model":"GigaChat-2","choices":[{"index":0,"message":{"role":"assistant","content":null,"function_call":{"name":"get_weather","arguments":{"city":"Moscow"}}},"finish_reason":"function_call"}],"usage":{"prompt_tokens":3,"completion_tokens":4,"total_tokens":7}}""");
        });

        var response = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "GigaChat-2",
            ToolChoice = "auto",
            Tools =
            [
                JsonSerializer.SerializeToElement(new
                {
                    type = "function",
                    function = new
                    {
                        name = "get_weather",
                        description = "Get the weather",
                        parameters = new { type = "object", properties = new { city = new { type = "string" } } }
                    }
                })
            ],
            Messages =
            [
                new ChatMessage { Role = "user", Content = JsonSerializer.SerializeToElement("Weather in Moscow?") },
                new ChatMessage
                {
                    Role = "assistant",
                    Content = JsonSerializer.SerializeToElement<object?>(null),
                    ToolCalls =
                    [
                        JsonSerializer.SerializeToElement(new
                        {
                            id = "call-weather",
                            type = "function",
                            function = new { name = "get_weather", arguments = """{"city":"Moscow"}""" }
                        })
                    ]
                },
                new ChatMessage
                {
                    Role = "tool",
                    ToolCallId = "call-weather",
                    Content = JsonSerializer.SerializeToElement("Sunny")
                }
            ]
        });

        Assert.Equal("/v1/chat/completions", chatRequest!.RequestUri!.AbsolutePath);
        var choice = Assert.IsType<JsonElement>(Assert.Single(response.Choices));
        Assert.Equal("tool_calls", choice.GetProperty("finish_reason").GetString());
        var toolCall = Assert.Single(choice.GetProperty("message").GetProperty("tool_calls").EnumerateArray());
        Assert.Equal("get_weather", toolCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"Moscow"}""", toolCall.GetProperty("function").GetProperty("arguments").GetString());
        Assert.Equal(7, JsonDocument.Parse(JsonSerializer.Serialize(response.Usage))
            .RootElement.GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task CompleteChatStreaming_maps_function_call_deltas_and_finish_reason()
    {
        var provider = CreateProvider("encoded-key", request =>
        {
            if (request.RequestUri!.Host == "ngw.devices.sberbank.ru")
                return JsonResponse("""{"access_token":"access-token"}""");

            Assert.Equal("api.giga.chat", request.RequestUri!.Host);
            Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
            using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            Assert.Equal("true", body.RootElement.GetProperty("stream").GetRawText());
            Assert.Equal("get_weather", body.RootElement.GetProperty("functions")[0].GetProperty("name").GetString());

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"id\":\"chatcmpl-3\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[{\"index\":0,\"delta\":{\"function_call\":{\"name\":\"get_weather\"}},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-3\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[{\"index\":0,\"delta\":{\"function_call\":{\"arguments\":\"{\\\"city\\\":\\\"Moscow\\\"}\"}},\"finish_reason\":null}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-3\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"function_call\"}]}\n\n" +
                    "data: {\"id\":\"chatcmpl-3\",\"object\":\"chat.completion.chunk\",\"created\":1768996171,\"model\":\"GigaChat-2\",\"choices\":[],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":3,\"total_tokens\":5}}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8,
                    "text/event-stream")
            };
        });

        var updates = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new ChatCompletionOptions
                       {
                           Model = "GigaChat-2",
                           Tools =
                           [
                               JsonSerializer.SerializeToElement(new
                               {
                                   type = "function",
                                   function = new { name = "get_weather", parameters = new { type = "object" } }
                               })
                           ],
                           Messages =
                           [
                               new ChatMessage
                               {
                                   Role = "user",
                                   Content = JsonSerializer.SerializeToElement("Weather in Moscow?")
                               }
                           ]
                       }))
            updates.Add(update);

        Assert.Equal(4, updates.Count);
        var initialCall = Assert.IsType<JsonElement>(Assert.Single(updates[0].Choices))
            .GetProperty("delta").GetProperty("tool_calls")[0];
        var argumentsCall = Assert.IsType<JsonElement>(Assert.Single(updates[1].Choices))
            .GetProperty("delta").GetProperty("tool_calls")[0];
        Assert.Equal(initialCall.GetProperty("id").GetString(), argumentsCall.GetProperty("id").GetString());
        Assert.Equal("get_weather", initialCall.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("""{"city":"Moscow"}""",
            argumentsCall.GetProperty("function").GetProperty("arguments").GetString());
        var finish = Assert.IsType<JsonElement>(Assert.Single(updates[2].Choices));
        Assert.Equal("tool_calls", finish.GetProperty("finish_reason").GetString());
        Assert.Equal(5, Assert.IsType<JsonElement>(updates[3].Usage).GetProperty("total_tokens").GetInt32());
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
