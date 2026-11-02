using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Runway;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Runway;

public sealed class RunwayProviderImageMetadataTests
{
    [Fact]
    public async Task Image_request_forwards_provider_options_to_the_upstream_root()
    {
        const string metadataJson = """
            {
              "contentModeration": {
                "publicFigureThreshold": "low",
                "futureModeration": {"enabled": true, "levels": [1, null, "high"]}
              },
              "future_config": {
                "values": [1, null, {"enabled": true}],
                "explicit_null": null
              },
              "future_array": [true, "text", 2.5],
              "future_count": 3,
              "future_enabled": true,
              "future_name": "native",
              "future_null": null
            }
            """;

        using var metadataDocument = JsonDocument.Parse(metadataJson);
        JsonElement? observedPayload = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/text_to_image")
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                observedPayload = body.RootElement.Clone();
                return JsonResponse("""{"id":"task-1"}""");
            }

            if (request.RequestUri.AbsolutePath == "/v1/tasks/task-1")
                return JsonResponse("""{"status":"SUCCEEDED","output":["https://images.example/output.png"]}""");

            if (request.RequestUri.AbsolutePath == "/output.png")
                return ImageResponse([1, 2, 3]);

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var request = new ImageRequest
        {
            Model = "gen4_image",
            Prompt = "A city at sunset",
            Seed = 123,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["runway"] = metadataDocument.RootElement.Clone()
            }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.True(JsonElement.DeepEquals(
            metadataDocument.RootElement,
            clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("runway")));

        await provider.ImageRequest(request);

        var root = Assert.IsType<JsonElement>(observedPayload);
        Assert.Equal("A city at sunset", root.GetProperty("promptText").GetString());
        Assert.Equal("gen4_image", root.GetProperty("model").GetString());
        Assert.Equal("1024:1024", root.GetProperty("ratio").GetString());
        Assert.Equal(123, root.GetProperty("seed").GetInt32());
        Assert.Equal("low", root.GetProperty("contentModeration").GetProperty("publicFigureThreshold").GetString());
        Assert.True(root.GetProperty("contentModeration").GetProperty("futureModeration").GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null,
            root.GetProperty("future_config").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("future_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("future_array")[0].ValueKind);
        Assert.Equal(3, root.GetProperty("future_count").GetInt32());
        Assert.True(root.GetProperty("future_enabled").GetBoolean());
        Assert.Equal("native", root.GetProperty("future_name").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("runway", out _));
    }

    [Fact]
    public async Task Image_request_preserves_defaults_and_known_null_omission()
    {
        JsonElement? observedPayload = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/v1/text_to_image")
            {
                using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                observedPayload = body.RootElement.Clone();
                return JsonResponse("""{"id":"task-1"}""");
            }

            if (request.RequestUri.AbsolutePath == "/v1/tasks/task-1")
                return JsonResponse("""{"status":"SUCCEEDED","output":["https://images.example/output.png"]}""");

            if (request.RequestUri.AbsolutePath == "/output.png")
                return ImageResponse([1]);

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        });

        var request = new ImageRequest
        {
            Model = "gen4_image",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["runway"] = JsonDocument.Parse(
                    """{"contentModeration":{"publicFigureThreshold":null},"future_null":null}""")
                    .RootElement.Clone()
            }
        };

        await provider.ImageRequest(request);

        var root = Assert.IsType<JsonElement>(observedPayload);
        Assert.Equal("1024:1024", root.GetProperty("ratio").GetString());
        Assert.False(root.TryGetProperty("seed", out _));
        Assert.Empty(root.GetProperty("contentModeration").EnumerateObject());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.Equal(5, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Image_request_preserves_known_metadata_validation_and_upstream_errors()
    {
        var provider = CreateProvider(_ =>
            throw new InvalidOperationException("Request must not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "gen4_image",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["runway"] = JsonDocument.Parse(
                    """{"contentModeration":{"publicFigureThreshold":42}}""").RootElement.Clone()
            }
        }));

        provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<Exception>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "gen4_image",
            Prompt = "A city at sunset"
        }));

        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    private static RunwayProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(
            new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };
        response.Content.Headers.ContentType = new("image/png");
        return response;
    }

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
            => Task.FromResult(responder(request));
    }
}
