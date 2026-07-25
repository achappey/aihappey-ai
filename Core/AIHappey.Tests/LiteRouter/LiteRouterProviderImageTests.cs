using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.LiteRouter;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.LiteRouter;

public sealed class LiteRouterProviderImageTests
{
    [Fact]
    public async Task Image_request_passes_raw_metadata_and_returns_the_jpeg_as_a_data_url()
    {
        var handler = new ImageResponseHandler();
        var provider = CreateProvider(handler);
        var request = new ImageRequest
        {
            Model = "sdxl-turbo",
            Prompt = "a serene mountain lake at sunset",
            Size = "768x512",
            Seed = 42,
            ProviderOptions = new()
            {
                ["literouter"] = JsonSerializer.SerializeToElement(new { custom_option = "preserved", width = 128 })
            }
        };

        var response = await provider.ImageRequest(request);

        Assert.Equal("https://image.literouter.com/generate", handler.RequestUri);
        Assert.Equal("Bearer", handler.Authorization?.Scheme);
        Assert.Equal("test-key", handler.Authorization?.Parameter);
        var payload = handler.Payload!.Value;
        Assert.Equal("a serene mountain lake at sunset", payload.GetProperty("prompt").GetString());
        Assert.Equal("sdxl-turbo", payload.GetProperty("model").GetString());
        Assert.Equal(768, payload.GetProperty("width").GetInt32());
        Assert.Equal(512, payload.GetProperty("height").GetInt32());
        Assert.Equal(42, payload.GetProperty("seed").GetInt32());
        Assert.Equal("preserved", payload.GetProperty("custom_option").GetString());
        Assert.Equal(["data:image/jpeg;base64,/9j/"], response.Images);
        Assert.Equal("sdxl-turbo", response.ProviderMetadata!["literouter"].GetProperty("X-Model").GetString());
        Assert.Equal("123", response.ProviderMetadata["literouter"].GetProperty("X-Seed").GetString());
        Assert.Equal("request-1", response.ProviderMetadata["literouter"].GetProperty("X-Request-ID").GetString());
        Assert.Equal("literouter/sdxl-turbo", response.Response.ModelId);
        Assert.Equal("request-1", response.Response.Headers!["X-Request-ID"]);
    }

    [Fact]
    public async Task Image_request_warns_but_generates_when_files_or_a_mask_are_provided()
    {
        var provider = CreateProvider(new ImageResponseHandler());

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "sdxl-turbo",
            Prompt = "generate anyway",
            Files = [new() { MediaType = "image/png", Data = "ZmFrZQ==" }],
            Mask = new() { MediaType = "image/png", Data = "ZmFrZQ==" }
        });

        Assert.Equal(2, response.Warnings.Count());
        Assert.Contains("files", JsonSerializer.Serialize(response.Warnings.ElementAt(0)), StringComparison.Ordinal);
        Assert.Contains("mask", JsonSerializer.Serialize(response.Warnings.ElementAt(1)), StringComparison.Ordinal);
        Assert.Single(response.Images!);
    }

    [Fact]
    public async Task OpenAI_generation_streaming_is_synthesized_from_the_binary_image_response()
    {
        var provider = CreateProvider(new ImageResponseHandler());
        var events = new List<IOpenAIImageStreamEvent>();

        await foreach (var streamEvent in provider.OpenAIImageGenerationStreamingAsync(new()
        {
            Model = "sdxl-turbo",
            Prompt = "stream a jpeg"
        }))
        {
            events.Add(streamEvent);
        }

        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("/9j/", completed.B64Json);
        Assert.Equal("image_generation.completed", completed.Type);
    }

    [Fact]
    public void OpenAI_image_edits_are_explicitly_unsupported()
    {
        var provider = CreateProvider(new ImageResponseHandler());

        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(new()).GetAwaiter().GetResult());
        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditStreamingAsync(new()).GetAsyncEnumerator());
    }

    private static LiteRouterProvider CreateProvider(HttpMessageHandler handler)
        => new(
            new TestApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new TestHttpClientFactory(handler));

    private sealed class ImageResponseHandler : HttpMessageHandler
    {
        public string? RequestUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        public JsonElement? Payload { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri?.ToString();
            Authorization = request.Headers.Authorization;
            Payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)).RootElement.Clone();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([0xff, 0xd8, 0xff])
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("image/jpeg");
            response.Headers.Add("X-Model", "sdxl-turbo");
            response.Headers.Add("X-Seed", "123");
            response.Headers.Add("X-Request-ID", "request-1");
            return response;
        }
    }

    private sealed class TestApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
