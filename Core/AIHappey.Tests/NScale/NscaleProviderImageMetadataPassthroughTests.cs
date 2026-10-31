using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Nscale;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.NScale;

public sealed class NscaleProviderImageMetadataPassthroughTests
{
    [Fact]
    public async Task Image_request_forwards_provider_options_at_upstream_root_and_preserves_known_fields()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/images/generations", request.RequestUri?.AbsolutePath);
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var options = JsonDocument.Parse(
            """
            {
              "prompt": "must not override request prompt",
              "model": "must not override request model",
              "quality": "high",
              "future_config": {
                "nested": [{"enabled": true}, [null, 2.5, "value"]],
                "explicit_null": null
              },
              "future_array": [1, false, {"deep": ["unchanged"]}],
              "future_string": "native",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """).RootElement.Clone();
        var request = new ImageRequest
        {
            Model = "nscale/image-model",
            Prompt = "A city at sunset",
            Size = "1024x1024",
            N = 2,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["nscale"] = options,
                ["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not-forwarded" })
            }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.True(clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("nscale")
            .TryGetProperty("future_config", out _));

        await provider.ImageRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("nscale/image-model", root.GetProperty("model").GetString());
        Assert.Equal("A city at sunset", root.GetProperty("prompt").GetString());
        Assert.Equal("1024x1024", root.GetProperty("size").GetString());
        Assert.Equal(2, root.GetProperty("n").GetInt32());
        Assert.Equal("high", root.GetProperty("quality").GetString());
        Assert.True(root.GetProperty("future_config").GetProperty("nested")[0].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("nested")[1][0].ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal("unchanged", root.GetProperty("future_array")[2].GetProperty("deep")[0].GetString());
        Assert.Equal("native", root.GetProperty("future_string").GetString());
        Assert.Equal("12345678901234567890.123456789", root.GetProperty("future_number").GetRawText());
        Assert.False(root.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("nscale", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
        Assert.False(root.TryGetProperty("secret", out _));
        Assert.Equal(11, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Image_request_omits_optional_null_fields_without_provider_options()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });

        await provider.ImageRequest(new ImageRequest
        {
            Model = "nscale/image-model",
            Prompt = "A city at sunset"
        });

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal(2, root.EnumerateObject().Count());
        Assert.False(root.TryGetProperty("n", out _));
        Assert.False(root.TryGetProperty("size", out _));
    }

    [Fact]
    public async Task Image_request_preserves_upstream_error_details()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":{"message":"invalid image request"}}""")
        });

        var error = await Assert.ThrowsAsync<Exception>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "nscale/image-model",
            Prompt = "A city at sunset"
        }));

        Assert.Equal("invalid image request", error.Message);
    }

    private static NscaleProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new SingleClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
