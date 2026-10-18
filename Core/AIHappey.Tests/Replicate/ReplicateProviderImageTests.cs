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

    private static ImageRequest CreateRequest()
        => new() { Model = "black-forest-labs/flux-schnell", Prompt = "A cat" };

    private static HttpResponseMessage Json(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

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
