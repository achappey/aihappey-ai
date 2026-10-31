using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Zai;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Zai;

public sealed class ZaiProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Image_request_forwards_unknown_provider_metadata_at_the_upstream_root()
    {
        string? requestBody = null;
        var provider = CreateProvider(
            request =>
            {
                if (request.Method == HttpMethod.Get)
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent([1, 2, 3])
                    };

                Assert.Equal("/api/paas/v4/images/generations", request.RequestUri?.AbsolutePath);
                requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return JsonResponse(new { data = new[] { new { url = "https://images.example/generated.png" } } });
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var metadata = JsonDocument.Parse(
            """
            {
              "quality": " STANDARD ",
              "future_config": {
                "values": [1, null, {"enabled": true}],
                "explicit_null": null
              },
              "future_count": 3,
              "future_enabled": true,
              "future_name": "native"
            }
            """).RootElement.Clone();
        var request = new ImageRequest
        {
            Model = "glm-image",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement> { ["zai"] = metadata }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.Equal(" STANDARD ", clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("zai").GetProperty("quality").GetString());

        await provider.ImageRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("standard", root.GetProperty("quality").GetString());
        Assert.Equal(1, root.GetProperty("future_config").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("future_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal(3, root.GetProperty("future_count").GetInt32());
        Assert.True(root.GetProperty("future_enabled").GetBoolean());
        Assert.Equal("native", root.GetProperty("future_name").GetString());
        Assert.False(root.TryGetProperty("providerMetadata", out _));
        Assert.False(root.TryGetProperty("zai", out _));
    }

    [Fact]
    public async Task Image_request_keeps_default_quality_and_invalid_quality_fallback()
    {
        foreach (var metadata in new JsonElement?[]
                 {
                     null,
                     JsonSerializer.SerializeToElement(new { quality = "unsupported" })
                 })
        {
            string? requestBody = null;
            var provider = CreateProvider(
                request =>
                {
                    if (request.Method == HttpMethod.Get)
                        return new HttpResponseMessage(HttpStatusCode.OK)
                        {
                            Content = new ByteArrayContent([1])
                        };

                    requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                    return JsonResponse(new { data = new[] { new { url = "https://images.example/generated.png" } } });
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound));

            var request = new ImageRequest
            {
                Model = "glm-image",
                Prompt = "A city at sunset",
                ProviderOptions = metadata is JsonElement value
                    ? new Dictionary<string, JsonElement> { ["zai"] = value }
                    : null
            };

            await provider.ImageRequest(request);

            using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
            Assert.Equal("hd", payload.RootElement.GetProperty("quality").GetString());
            Assert.Equal("1280x1280", payload.RootElement.GetProperty("size").GetString());
            Assert.Equal(4, payload.RootElement.EnumerateObject().Count());
        }
    }

    [Fact]
    public async Task Video_request_forwards_unknown_provider_options_and_keeps_known_mappings()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { id = "video-task-1" });
        });

        var options = JsonDocument.Parse(
            """
            {
              "style": "cinematic",
              "movement_amplitude": "high",
              "future_config": {
                "values": [1, null, {"enabled": true}],
                "explicit_null": null
              },
              "future_count": 3,
              "future_enabled": true,
              "future_name": "native"
            }
            """).RootElement.Clone();
        var request = new VideoRequest
        {
            Model = "viduq1-text",
            Prompt = "A mountain landscape",
            ProviderOptions = new Dictionary<string, JsonElement> { ["zai"] = options }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.True(clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("zai").TryGetProperty("future_config", out _));

        await provider.StartVideoOperation(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("cinematic", root.GetProperty("style").GetString());
        Assert.Equal("high", root.GetProperty("movement_amplitude").GetString());
        Assert.Equal(1, root.GetProperty("future_config").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("future_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal(3, root.GetProperty("future_count").GetInt32());
        Assert.True(root.GetProperty("future_enabled").GetBoolean());
        Assert.Equal("native", root.GetProperty("future_name").GetString());
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("zai", out _));
    }

    [Fact]
    public async Task Video_request_preserves_invalid_known_metadata_error_behavior()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new VideoRequest
        {
            Model = "viduq1-text",
            Prompt = "A mountain landscape",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["zai"] = JsonSerializer.SerializeToElement(new { style = 42 })
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => provider.StartVideoOperation(request));
    }

    [Fact]
    public async Task Image_request_preserves_upstream_error_details()
    {
        var provider = CreateProvider(
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("upstream failure")
            });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.ImageRequest(new ImageRequest
            {
                Model = "glm-image",
                Prompt = "A city at sunset"
            }));

        Assert.Equal("Z.AI image generation failed (400): upstream failure", error.Message);
    }

    private static ZaiProvider CreateProvider(params Func<HttpRequestMessage, HttpResponseMessage>[] responders)
    {
        var clients = responders.Select(responder =>
            new HttpClient(new DelegateHttpMessageHandler(responder))).ToArray();
        if (clients.Length == 1)
            clients = [clients[0], new HttpClient(new DelegateHttpMessageHandler(
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)))];

        return new ZaiProvider(new StaticApiKeyResolver(), new SequencedHttpClientFactory(clients));
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SequencedHttpClientFactory(params HttpClient[] clients) : IHttpClientFactory
    {
        private int _index;

        public HttpClient CreateClient(string name)
        {
            Assert.True(_index < clients.Length, "The provider requested more HTTP clients than expected.");
            return clients[_index++];
        }
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
