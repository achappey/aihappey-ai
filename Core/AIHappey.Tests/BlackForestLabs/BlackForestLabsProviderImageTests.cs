using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.BlackForestLabs;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.BlackForestLabs;

public sealed class BlackForestLabsProviderImageTests
{
    [Fact]
    public async Task ImageRequest_UsesRegionalPollingUrlsAndSumsSubmitCosts()
    {
        var submitCount = 0;
        var requestedUris = new List<Uri>();
        var provider = CreateProvider(request =>
        {
            requestedUris.Add(request.RequestUri!);
            if (request.Method == HttpMethod.Post)
            {
                submitCount++;
                var id = $"task-{submitCount}";
                return Json(HttpStatusCode.OK,
                    JsonSerializer.Serialize(new
                    {
                        id,
                        polling_url = $"https://api.us4.bfl.ai/v1/get_result?id={id}",
                        cost = submitCount + 0.25m
                    }));
            }

            var taskId = request.RequestUri!.Query.Split('=').Last();
            return Json(HttpStatusCode.OK,
                JsonSerializer.Serialize(new
                {
                    id = taskId,
                    status = "Ready",
                    result = new { sample = "data:image/png;base64,AQID" }
                }));
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "flux-2-pro",
            Prompt = "A test image",
            N = 2
        });

        Assert.Equal(2, response.Images!.Count());
        Assert.Equal(2, requestedUris.Count(uri => uri.Host == "api.us4.bfl.ai"));
        var providerMetadata = Assert.IsType<Dictionary<string, JsonElement>>(response.ProviderMetadata);
        Assert.Equal(0.035m, providerMetadata["gateway"].GetProperty("cost").GetDecimal());

        var raw = providerMetadata["blackforestlabs"];
        Assert.Equal(JsonValueKind.Array, raw.ValueKind);
        Assert.Equal(2, raw.GetArrayLength());
        Assert.Equal("task-1", raw[0].GetProperty("submit").GetProperty("id").GetString());
        Assert.Equal("Ready", raw[0].GetProperty("result").GetProperty("status").GetString());
    }

    [Fact]
    public async Task ImageRequest_FallsBackToCanonicalResultEndpointWhenPollingUrlIsMissing()
    {
        Uri? pollUri = null;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Json(HttpStatusCode.OK, """{"id":"fallback-task","cost":0.5}""");

            pollUri = request.RequestUri;
            return Json(HttpStatusCode.OK,
                """{"id":"fallback-task","status":"Ready","result":{"sample":"data:image/png;base64,AQID"}}""");
        });

        await provider.ImageRequest(new ImageRequest { Model = "flux-2-pro", Prompt = "A test image" });

        Assert.Equal("https://api.bfl.ai/v1/get_result?id=fallback-task", pollUri?.AbsoluteUri);
    }

    [Theory]
    [InlineData("flux-tools/outpainting-v1", "/v1/flux-tools/outpainting-v1")]
    [InlineData("flux-tools/erase-v1", "/v1/flux-tools/erase-v1")]
    [InlineData("flux-tools/deblur-v1", "/v1/flux-tools/deblur-v1")]
    public async Task ImageRequest_RoutesFluxToolsAndMapsSpecializedPayloads(string model, string expectedPath)
    {
        Uri? submitUri = null;
        JsonElement payload = default;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                submitUri = request.RequestUri;
                payload = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
                return Json(HttpStatusCode.OK, """{"id":"tool-task"}""");
            }

            return Json(HttpStatusCode.OK,
                """{"id":"tool-task","status":"Ready","result":{"sample":"data:image/png;base64,AQID"}}""");
        });

        var request = new ImageRequest
        {
            Model = model,
            Prompt = model.EndsWith("outpainting-v1", StringComparison.Ordinal) ? "Extend the sky" : null!,
            Size = "1024x768",
            Seed = 42,
            Files = [new ImageFile { MediaType = "image/png", Data = "data:image/png;base64,SOURCE" }],
            Mask = model.EndsWith("erase-v1", StringComparison.Ordinal)
                ? new ImageFile { MediaType = "image/png", Data = "data:image/png;base64,MASK" }
                : null,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["blackforestlabs"] = JsonSerializer.SerializeToElement(new { output_format = "webp", safety_tolerance = 3 })
            }
        };

        await provider.ImageRequest(request);

        Assert.Equal(expectedPath, submitUri?.AbsolutePath);
        Assert.Equal("webp", payload.GetProperty("output_format").GetString());
        Assert.Equal(3, payload.GetProperty("safety_tolerance").GetInt32());

        if (model.EndsWith("outpainting-v1", StringComparison.Ordinal))
        {
            Assert.Equal("SOURCE", payload.GetProperty("input_image").GetString());
            Assert.Equal(1024, payload.GetProperty("width").GetInt32());
            Assert.Equal(768, payload.GetProperty("height").GetInt32());
            Assert.Equal("Extend the sky", payload.GetProperty("prompt").GetString());
            Assert.False(payload.TryGetProperty("seed", out _));
        }
        else
        {
            Assert.Equal("SOURCE", payload.GetProperty("image").GetString());
            Assert.Equal(42, payload.GetProperty("seed").GetInt32());
            Assert.False(payload.TryGetProperty("prompt", out _));
            if (model.EndsWith("erase-v1", StringComparison.Ordinal))
                Assert.Equal("MASK", payload.GetProperty("mask").GetString());
        }
    }

    [Fact]
    public async Task ImageRequest_ValidatesFluxToolRequiredInputs()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("HTTP should not be called."));
        var source = new ImageFile { MediaType = "image/png", Data = "SOURCE" };

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "flux-tools/deblur-v1"
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "flux-tools/erase-v1",
            Files = [source]
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "flux-tools/outpainting-v1",
            Files = [source]
        }));
    }

    private static BlackForestLabsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(
            new HttpClient(new StaticResponseHttpMessageHandler(responder))));

    private static HttpResponseMessage Json(HttpStatusCode statusCode, string json)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
