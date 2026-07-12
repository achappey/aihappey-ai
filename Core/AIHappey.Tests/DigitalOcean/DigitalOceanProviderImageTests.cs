using System.Net;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.DigitalOcean;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.DigitalOcean;

public sealed class DigitalOceanProviderImageTests
{
    [Fact]
    public async Task ImageRequest_merges_provider_options_posts_generation_and_maps_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse(new
            {
                background = "opaque",
                created = 1677649456,
                output_format = "png",
                quality = "high",
                size = "1024x1536",
                data = new[]
                {
                    new { b64_json = Convert.ToBase64String(Encoding.UTF8.GetBytes("png-bytes")) }
                },
                usage = new { input_tokens = 11, output_tokens = 22, total_tokens = 33 }
            });
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "openai-gpt-image-1",
            Prompt = "A cute baby sea otter floating on its back in calm blue water",
            Size = "1024x1536",
            N = 1,
            ProviderOptions = ProviderOptions(new
            {
                background = "auto",
                moderation = "auto",
                output_compression = 100,
                output_format = "png",
                partial_images = 1,
                prompt = "provider prompt should be replaced",
                quality = "auto",
                size = "auto",
                stream = false,
                user = "user-1234"
            })
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/images/generations", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        using var payloadDocument = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var payload = payloadDocument.RootElement;
        Assert.Equal("openai-gpt-image-1", payload.GetProperty("model").GetString());
        Assert.Equal("A cute baby sea otter floating on its back in calm blue water", payload.GetProperty("prompt").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal("1024x1536", payload.GetProperty("size").GetString());
        Assert.Equal("auto", payload.GetProperty("background").GetString());
        Assert.Equal("auto", payload.GetProperty("moderation").GetString());
        Assert.Equal(100, payload.GetProperty("output_compression").GetInt32());
        Assert.Equal("png", payload.GetProperty("output_format").GetString());
        Assert.Equal(1, payload.GetProperty("partial_images").GetInt32());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.Equal("user-1234", payload.GetProperty("user").GetString());

        var image = Assert.Single(response.Images ?? []);
        Assert.Equal($"data:{MediaTypeNames.Image.Png};base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes("png-bytes"))}", image);
        Assert.Equal(11, response.Usage?.InputTokens);
        Assert.Equal(22, response.Usage?.OutputTokens);
        Assert.Equal(33, response.Usage?.TotalTokens);
        Assert.Equal("digitalocean/openai-gpt-image-1", response.Response.ModelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1677649456).UtcDateTime, response.Response.Timestamp);

        var providerMetadata = Assert.Contains("digitalocean", response.ProviderMetadata ?? []);
        Assert.Equal("opaque", providerMetadata.GetProperty("background").GetString());
        Assert.Equal("png", providerMetadata.GetProperty("output_format").GetString());
        Assert.Equal("high", providerMetadata.GetProperty("quality").GetString());
        Assert.Equal("1024x1536", providerMetadata.GetProperty("size").GetString());
        Assert.Equal(33, providerMetadata.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task ImageRequest_rejects_streaming_provider_option()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "openai-gpt-image-1",
            Prompt = "A cute baby sea otter floating on its back in calm blue water",
            N = 1,
            ProviderOptions = ProviderOptions(new
            {
                stream = true,
                partial_images = 1
            })
        }));

        Assert.Contains("streaming", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static DigitalOceanProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new DigitalOceanProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(object metadata)
        => new()
        {
            ["digitalocean"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? MediaTypeNames.Application.Json);
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
