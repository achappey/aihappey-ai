using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.KlingAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.KlingAI;

public sealed class KlingAISpeechProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_request_preserves_keyed_metadata_and_forwards_unknown_json_at_the_request_root()
    {
        JsonElement payload = default;
        var metadata = ParseElement("""
            {
              "duration": 5.5,
              "callback_url": "https://callback.test/status",
              "external_task_id": "external-123",
              "prompt": "metadata prompt",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "klingai/speech-model",
            Text = "Request prompt",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["klingai"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/audio/text-to-audio", message.RequestUri!.AbsolutePath);
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"code":0,"data":{"task_id":"task-1"}}""");
            }

            if (message.Method == HttpMethod.Get && message.RequestUri!.AbsolutePath == "/v1/audio/text-to-audio/task-1")
                return JsonResponse("""{"code":0,"data":{"task_status":"succeed","task_result":{"audios":[{"url_mp3":"https://assets.test/audio.mp3"}]}}}""");

            Assert.Equal("https://assets.test/audio.mp3", message.RequestUri!.ToString());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });

        var response = await CreateProvider(client).SpeechRequest(request);

        Assert.True(JsonElement.DeepEquals(metadata, serializedClientRequest
            .GetProperty("providerOptions").GetProperty("klingai")));
        Assert.Equal("Request prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(5.5, payload.GetProperty("duration").GetDouble());
        Assert.Equal("https://callback.test/status", payload.GetProperty("callback_url").GetString());
        Assert.Equal("external-123", payload.GetProperty("external_task_id").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), payload.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), payload.GetProperty("future_array")));
        Assert.Equal("unchanged", payload.GetProperty("future_string").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_number"), payload.GetProperty("future_number")));
        Assert.False(payload.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("klingai", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.Equal(10, payload.EnumerateObject().Count());
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
    }

    [Fact]
    public async Task Speech_request_preserves_known_validation_null_omissions_and_errors()
    {
        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var provider = CreateProvider(unusedClient);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(CreateRequest("""{"future":true}""")));
        var rangeError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            provider.SpeechRequest(CreateRequest("""{"duration":2.9}""")));
        Assert.Equal("Duration", rangeError.ParamName);
        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(CreateRequest("""{"duration":"five"}""")));

        JsonElement payload = default;
        using var successfulClient = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"code":0,"data":{"task_id":"task-1"}}""");
            }

            if (message.RequestUri!.AbsolutePath == "/v1/audio/text-to-audio/task-1")
                return JsonResponse("""{"code":0,"data":{"task_status":"succeed","task_result":{"audios":[{"url_mp3":"https://assets.test/audio.mp3"}]}}}""");

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1]) };
        });
        await CreateProvider(successfulClient).SpeechRequest(CreateRequest(
            """{"duration":3,"callback_url":null,"external_task_id":null,"future_null":null}"""));
        Assert.False(payload.TryGetProperty("callback_url", out _));
        Assert.False(payload.TryGetProperty("external_task_id", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).SpeechRequest(CreateRequest("""{"duration":3}""")));
        Assert.Equal("KlingAI speech create failed (400): upstream failure", error.Message);
    }

    private static SpeechRequest CreateRequest(string metadata)
        => new()
        {
            Model = "klingai/speech-model",
            Text = "Request prompt",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["klingai"] = ParseElement(metadata)
            }
        };

    private static KlingAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "access-key:secret-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
