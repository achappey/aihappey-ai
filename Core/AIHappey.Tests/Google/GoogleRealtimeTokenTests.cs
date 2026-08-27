using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleRealtimeTokenTests
{
    [Fact]
    public async Task GetRealtimeToken_uses_documented_defaults_and_maps_response()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1beta/auth_tokens", request.RequestUri?.AbsolutePath);
            Assert.Equal("test-key", request.ApiKey);

            using var document = JsonDocument.Parse(request.Body!);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("uses").GetInt32());
            Assert.True(DateTimeOffset.TryParse(root.GetProperty("expireTime").GetString(), out _));

            var constraints = root.GetProperty("liveConnectConstraints");
            Assert.Equal("models/gemini-3.5-transcribe-live", constraints.GetProperty("model").GetString());
            var config = constraints.GetProperty("config");
            Assert.Equal("TEXT", config.GetProperty("responseModalities")[0].GetString());
            Assert.Empty(config.GetProperty("inputAudioTranscription").GetProperty("languageCodes").EnumerateArray());

            return JsonResponse(
                """{ "name": "auth_tokens/token-123", "expireTime": "2026-08-27T14:15:00Z" }""");
        });
        var provider = CreateProvider(handler);

        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "google/gemini-3.5-transcribe-live"
        }, CancellationToken.None);

        Assert.Equal("auth_tokens/token-123", response.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-08-27T14:15:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
    }

    [Fact]
    public async Task GetRealtimeToken_preserves_complete_provider_payload_but_overrides_model()
    {
        var handler = new RecordingHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Body!);
            var root = document.RootElement;
            Assert.Equal(3, root.GetProperty("uses").GetInt32());
            Assert.Equal("2026-08-27T15:00:00Z", root.GetProperty("expireTime").GetString());
            Assert.Equal("keep-me", root.GetProperty("customField").GetString());

            var constraints = root.GetProperty("liveConnectConstraints");
            Assert.Equal("models/gemini-live-custom", constraints.GetProperty("model").GetString());
            Assert.Equal("AUDIO", constraints.GetProperty("config").GetProperty("responseModalities")[0].GetString());

            return JsonResponse(
                """{ "name": "auth_tokens/custom", "expireTime": "2026-08-27T15:00:00Z" }""");
        });
        var provider = CreateProvider(handler);

        await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "models/gemini-live-custom",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    uses = 3,
                    expireTime = "2026-08-27T15:00:00Z",
                    customField = "keep-me",
                    liveConnectConstraints = new
                    {
                        model = "models/must-not-be-used",
                        config = new { responseModalities = new[] { "AUDIO" } }
                    }
                })
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GetRealtimeToken_surfaces_upstream_failure_body()
    {
        var provider = CreateProvider(new RecordingHandler(_ =>
            JsonResponse("""{ "error": { "message": "invalid constraints" } }""", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            new RealtimeRequest { Model = "gemini-live" },
            CancellationToken.None));

        Assert.Contains("invalid constraints", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}", "token name")]
    [InlineData("{ \"name\": \"auth_tokens/token\", \"expireTime\": \"invalid\" }", "expireTime")]
    public async Task GetRealtimeToken_rejects_invalid_success_response(string body, string expectedMessage)
    {
        var provider = CreateProvider(new RecordingHandler(_ => JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            new RealtimeRequest { Model = "gemini-live" },
            CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static GoogleAIProvider CreateProvider(HttpMessageHandler handler)
    {
        var client = new HttpClient(handler);
        return new GoogleAIProvider(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var recordedRequest = new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Headers.TryGetValues("x-goog-api-key", out var values) ? values.SingleOrDefault() : null,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));

            return responder(recordedRequest);
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? ApiKey, string? Body);
}
