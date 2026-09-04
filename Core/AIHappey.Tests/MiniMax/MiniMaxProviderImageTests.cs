using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MiniMax;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.MiniMax;

public sealed class MiniMaxProviderImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImageRequest_MapsNativePayloadAndPromptOptimizer(bool promptOptimizer)
    {
        JsonElement payload = default;
        AuthenticationHeaderValue? authorization = null;
        Uri? requestUri = null;
        var provider = CreateProvider(request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization;
            payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();

            return JsonResponse(new
            {
                id = "image-trace-id",
                data = new { image_base64 = new[] { "AQID" } },
                metadata = new { success_count = 1, failed_count = 0 },
                base_resp = new { status_code = 0, status_msg = "success" }
            });
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "minimax/image-01",
            Prompt = "A library window",
            Size = "1280x720",
            Seed = 42,
            N = 2,
            Files =
            [
                new ImageFile
                {
                    MediaType = "image/png",
                    Data = "https://images.example/portrait.png"
                }
            ],
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["minimax"] = JsonSerializer.SerializeToElement(new
                {
                    prompt_optimizer = promptOptimizer
                })
            }
        });

        Assert.Equal("/v1/image_generation", requestUri?.AbsolutePath);
        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-api-key", authorization?.Parameter);
        Assert.Equal("image-01", payload.GetProperty("model").GetString());
        Assert.Equal("A library window", payload.GetProperty("prompt").GetString());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal(1280, payload.GetProperty("width").GetInt32());
        Assert.Equal(720, payload.GetProperty("height").GetInt32());
        Assert.Equal("base64", payload.GetProperty("response_format").GetString());
        Assert.Equal(42, payload.GetProperty("seed").GetInt32());
        Assert.Equal(2, payload.GetProperty("n").GetInt32());
        Assert.Equal(promptOptimizer, payload.GetProperty("prompt_optimizer").GetBoolean());

        var subject = Assert.Single(payload.GetProperty("subject_reference").EnumerateArray());
        Assert.Equal("character", subject.GetProperty("type").GetString());
        Assert.Equal("https://images.example/portrait.png", subject.GetProperty("image_file").GetString());
        Assert.Equal("data:image/png;base64,AQID", Assert.Single(response.Images!));

        var metadata = Assert.IsType<Dictionary<string, JsonElement>>(response.ProviderMetadata);
        Assert.Equal("image-trace-id", metadata["minimax"].GetProperty("id").GetString());
    }

    [Fact]
    public async Task ImageRequest_UsesDataUrlForBase64SubjectAndOmitsUnsetPromptOptimizer()
    {
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())
                .RootElement.Clone();
            return SuccessfulImageResponse();
        });

        await provider.ImageRequest(new ImageRequest
        {
            Model = "image-01-live",
            Prompt = "A portrait",
            Files = [new ImageFile { MediaType = "image/jpeg", Data = "AQID" }]
        });

        var subject = Assert.Single(payload.GetProperty("subject_reference").EnumerateArray());
        Assert.Equal("data:image/jpeg;base64,AQID", subject.GetProperty("image_file").GetString());
        Assert.False(payload.TryGetProperty("prompt_optimizer", out _));
    }

    [Fact]
    public async Task ImageRequest_ThrowsDetailedErrorForMiniMaxBaseResponseFailure()
    {
        var provider = CreateProvider(_ => JsonResponse(new
        {
            id = "failed-trace-id",
            base_resp = new { status_code = 2013, status_msg = "invalid input" }
        }));

        var exception = await Assert.ThrowsAsync<Exception>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "image-01",
            Prompt = "A portrait"
        }));

        Assert.Contains("status_code=2013", exception.Message, StringComparison.Ordinal);
        Assert.Contains("invalid input", exception.Message, StringComparison.Ordinal);
        Assert.Contains("failed-trace-id", exception.Message, StringComparison.Ordinal);
    }

    private static MiniMaxProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static HttpResponseMessage SuccessfulImageResponse()
        => JsonResponse(new
        {
            data = new { image_base64 = new[] { "AQID" } },
            base_resp = new { status_code = 0, status_msg = "success" }
        });

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

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
