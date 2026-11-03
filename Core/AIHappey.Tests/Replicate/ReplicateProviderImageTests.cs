using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Replicate;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Replicate;

public sealed class ReplicateProviderImageTests
{
    private const string ImageDataUrl = "data:image/png;base64,aW1hZ2U=";

    [Fact]
    public async Task ImageRequest_polls_when_sync_wait_returns_a_pending_prediction()
    {
        var requests = new List<string>();
        var provider = CreateProvider(request =>
        {
            requests.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Post)
                return Json("""{"id":"prediction_123","status":"processing"}""");

            return Json(JsonSerializer.Serialize(new
            {
                id = "prediction_123",
                status = "succeeded",
                output = ImageDataUrl
            }));
        });

        var response = await provider.ImageRequest(CreateRequest());

        Assert.Equal(
            [
                "POST /v1/models/black-forest-labs/flux-schnell/predictions",
                "GET /v1/predictions/prediction_123"
            ],
            requests);
        Assert.Equal(ImageDataUrl, Assert.Single(response.Images!));
    }

    [Fact]
    public async Task ImageRequest_throws_when_a_polled_prediction_fails()
    {
        var postSent = false;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                postSent = true;
                return Json("""{"id":"prediction_123","status":"starting"}""");
            }

            return Json("""{"id":"prediction_123","status":"failed","error":"model error"}""");
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.ImageRequest(CreateRequest()));

        Assert.True(postSent);
        Assert.Contains("failed", exception.Message);
        Assert.Contains("model error", exception.Message);
    }

    [Fact]
    public async Task ImageRequest_cancellation_stops_prediction_polling()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
                return Json("""{"id":"prediction_123","status":"processing"}""");

            cancellation.Cancel();
            return Json("""{"id":"prediction_123","status":"processing"}""");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.ImageRequest(CreateRequest(), cancellation.Token));
    }

    [Fact]
    public async Task ImageRequest_forwards_unknown_provider_options_inside_input_and_preserves_override_precedence()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return Json("""{"output":"data:image/png;base64,aW1hZ2U="}""");
        });
        var imageRequest = CreateRequest();
        imageRequest.ProviderOptions = new Dictionary<string, JsonElement>
        {
            ["replicate"] = JsonDocument.Parse(
                """
                {
                  "preferWaitSeconds": 90,
                  "cancelAfter": "5m",
                  "steps": 32,
                  "future_config": {
                    "values": [1, null, { "enabled": true }],
                    "explicit_null": null
                  },
                  "future_enabled": true,
                  "future_name": "native",
                  "inputOverrides": {
                    "prompt": "override prompt",
                    "width": 512,
                    "new_nested": { "items": [false, null, "value"] }
                  }
                }
                """).RootElement.Clone(),
            ["another-provider"] = JsonSerializer.SerializeToElement(new { ignored = true })
        };

        await provider.ImageRequest(imageRequest);

        Assert.NotNull(capturedRequest);
        Assert.Equal("wait=60", capturedRequest!.Headers.GetValues("Prefer").Single());
        Assert.Equal("5m", capturedRequest.Headers.GetValues("Cancel-After").Single());
        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var body = document.RootElement;
        Assert.Equal(1, body.EnumerateObject().Count());
        Assert.False(body.TryGetProperty("providerOptions", out _));
        Assert.False(body.TryGetProperty("replicate", out _));
        var input = body.GetProperty("input");
        Assert.Equal("override prompt", input.GetProperty("prompt").GetString());
        Assert.Equal(512, input.GetProperty("width").GetInt32());
        Assert.Equal(1024, input.GetProperty("height").GetInt32());
        Assert.Equal(32, input.GetProperty("steps").GetInt32());
        Assert.Equal(1, input.GetProperty("future_config").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("future_config").GetProperty("values")[1].ValueKind);
        Assert.True(input.GetProperty("future_config").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, input.GetProperty("future_config").GetProperty("explicit_null").ValueKind);
        Assert.True(input.GetProperty("future_enabled").GetBoolean());
        Assert.Equal("native", input.GetProperty("future_name").GetString());
        Assert.False(input.TryGetProperty("ignored", out _));
        Assert.Equal(JsonValueKind.Null, input.GetProperty("new_nested").GetProperty("items")[1].ValueKind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"another-provider":{"must_not_leak":true}}""")]
    [InlineData("""{"replicate":null}""")]
    [InlineData("""{"replicate":{}}""")]
    public async Task ImageRequest_uses_defaults_without_provider_metadata(string? providerOptionsJson)
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return Json("""{"output":"data:image/png;base64,aW1hZ2U="}""");
        });

        var imageRequest = CreateRequest();
        imageRequest.ProviderOptions = providerOptionsJson is null
            ? null
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(providerOptionsJson);

        var response = await provider.ImageRequest(imageRequest);

        Assert.Equal("wait=60", capturedRequest!.Headers.GetValues("Prefer").Single());
        Assert.False(capturedRequest.Headers.Contains("Cancel-After"));
        using var document = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        var input = document.RootElement.GetProperty("input");
        Assert.Equal("A cat", input.GetProperty("prompt").GetString());
        Assert.Equal(1024, input.GetProperty("width").GetInt32());
        Assert.Equal(1024, input.GetProperty("height").GetInt32());
        Assert.Equal(3, input.EnumerateObject().Count());
        Assert.Equal(ImageDataUrl, Assert.Single(response.Images!));
    }

    [Fact]
    public async Task ImageRequest_preserves_input_override_validation()
    {
        var invalidProvider = CreateProvider(_ =>
            throw new InvalidOperationException("Invalid inputOverrides must not be sent."));
        var invalidRequest = CreateRequest();
        invalidRequest.ProviderOptions = new Dictionary<string, JsonElement>
        {
            ["replicate"] = JsonSerializer.SerializeToElement(new { inputOverrides = 5 })
        };

        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.ImageRequest(invalidRequest));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"invalid\"")]
    [InlineData("42")]
    [InlineData("true")]
    public async Task ImageRequest_rejects_non_object_provider_metadata_before_sending(string metadataJson)
    {
        var requestsSent = 0;
        var provider = CreateProvider(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Invalid metadata must not be sent upstream.");
        });
        var imageRequest = CreateRequest();
        imageRequest.ProviderOptions = new Dictionary<string, JsonElement>
        {
            ["replicate"] = JsonSerializer.Deserialize<JsonElement>(metadataJson)
        };

        var exception = await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(imageRequest));

        Assert.Equal("Replicate image provider metadata must be a JSON object or null.", exception.Message);
        Assert.Equal(0, requestsSent);
    }

    private static ImageRequest CreateRequest()
        => new() { Model = "black-forest-labs/flux-schnell", Prompt = "A cat" };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (request.Content is not null)
            clone.Content = new StringContent(request.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json");
        return clone;
    }

    private static ReplicateProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
