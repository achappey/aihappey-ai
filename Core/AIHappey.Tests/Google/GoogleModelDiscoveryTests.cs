using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Google;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleModelDiscoveryTests
{
    [Fact]
    public async Task ListModelsFollowsNextPageToken()
    {
        var handler = new RecordingHandler(
        [
            JsonResponse(new
            {
                models = new[] { new { name = "models/gemini-2.5-flash", displayName = "Gemini 2.5 Flash" } },
                nextPageToken = "next token"
            }),
            JsonResponse(new
            {
                models = new[] { new { name = "models/gemini-2.5-pro", displayName = "Gemini 2.5 Pro" } }
            })
        ]);
        var provider = new GoogleAIProvider(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(new HttpClient(handler)));

        var models = (await provider.ListModels()).ToList();

        Assert.Equal(
            ["google/gemini-2.5-flash", "google/gemini-2.5-pro"],
            models.Select(model => model.Id));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1beta/models", request.Uri!.AbsolutePath);
            Assert.Equal("test-key", request.ApiKey);
            Assert.Contains("pageSize=1000", Uri.UnescapeDataString(request.Uri.Query));
        });
        Assert.DoesNotContain("pageToken=", Uri.UnescapeDataString(handler.Requests[0].Uri!.Query));
        Assert.Contains("pageToken=next token", Uri.UnescapeDataString(handler.Requests[1].Uri!.Query));
    }

    [Theory]
    [InlineData("models/imagen-4.0-generate-001", true)]
    [InlineData("imagen-3.0-generate-002", true)]
    [InlineData("models/gemini-3.1-flash-image", false)]
    [InlineData(null, false)]
    public void IsImagenModelRecognizesModelIdsInGoogleResourceNames(string? name, bool expected)
    {
        var method = typeof(GoogleAIProvider).GetMethod("IsImagenModel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "IsImagenModel");

        Assert.Equal(expected, method.Invoke(null, [name]));
    }

    [Theory]
    [InlineData("gemini-omni-flash-preview", true)]
    [InlineData("models/gemini-omni-flash-preview", true)]
    [InlineData("google/gemini-omni-flash-preview", true)]
    [InlineData("google/GEMINI-OMNI-FLASH-PREVIEW", true)]
    [InlineData("google/gemini-omni-1.1-flash", false)]
    [InlineData("models/gemini-3-flash-preview", false)]
    [InlineData(null, false)]
    public void IsDeprecatedGoogleOmniModelOnlyFiltersTheDeprecatedPreview(string? name, bool expected)
    {
        var method = typeof(GoogleAIProvider).GetMethod("IsDeprecatedGoogleOmniModel", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "IsDeprecatedGoogleOmniModel");

        Assert.Equal(expected, method.Invoke(null, [name]));
    }

    private static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.TryGetValues("x-goog-api-key", out var keys) ? keys.Single() : null));
            return _responses.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? ApiKey);
}
