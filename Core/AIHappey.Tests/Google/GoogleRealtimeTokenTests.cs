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
    public async Task GetRealtimeToken_raw_passes_wire_payload_and_maps_response()
    {
        const string expireTime = "2026-08-27T14:15:00Z";
        const string newSessionExpireTime = "2026-08-27T13:46:00Z";
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1alpha/auth_tokens", request.RequestUri?.AbsolutePath);
            Assert.Equal("test-key", request.ApiKey);

            using var document = JsonDocument.Parse(request.Body!);
            var root = document.RootElement;
            Assert.Equal(1, root.GetProperty("uses").GetInt32());
            Assert.Equal(expireTime, root.GetProperty("expireTime").GetString());
            Assert.Equal(newSessionExpireTime, root.GetProperty("newSessionExpireTime").GetString());
            Assert.Equal("setup.model,setup.generation_config.response_modalities", root.GetProperty("fieldMask").GetString());

            var setup = root.GetProperty("bidiGenerateContentSetup");
            Assert.Equal("models/gemini-3.5-transcribe-live", setup.GetProperty("model").GetString());
            Assert.Equal("TEXT", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());
            Assert.Empty(setup.GetProperty("inputAudioTranscription").GetProperty("languageCodes").EnumerateArray());

            // expireTime is input-only in Google's AuthToken schema, so the
            // production response commonly contains only the token name.
            return JsonResponse("""{ "name": "auth_tokens/token-123" }""");
        });
        var provider = CreateProvider(handler);

        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "google/gemini-3.5-transcribe-live",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    uses = 1,
                    expireTime,
                    newSessionExpireTime,
                    fieldMask = "setup.model,setup.generation_config.response_modalities",
                    bidiGenerateContentSetup = new
                    {
                        model = "models/gemini-3.5-transcribe-live",
                        generationConfig = new { responseModalities = new[] { "TEXT" } },
                        inputAudioTranscription = new { languageCodes = Array.Empty<string>() }
                    }
                })
            }
        }, CancellationToken.None);

        Assert.Equal("auth_tokens/token-123", response.Value);
        Assert.Equal(DateTimeOffset.Parse("2026-08-27T14:15:00Z").ToUnixTimeSeconds(), response.ExpiresAt);
    }

    [Fact]
    public async Task GetRealtimeToken_does_not_rewrite_client_owned_model_or_custom_fields()
    {
        var handler = new RecordingHandler(request =>
        {
            using var document = JsonDocument.Parse(request.Body!);
            var root = document.RootElement;
            Assert.Equal(3, root.GetProperty("uses").GetInt32());
            Assert.Equal("2026-08-27T15:00:00Z", root.GetProperty("expireTime").GetString());
            Assert.Equal("keep-me", root.GetProperty("customField").GetString());

            var setup = root.GetProperty("bidiGenerateContentSetup");
            Assert.Equal("models/client-owned-model", setup.GetProperty("model").GetString());
            Assert.Equal("AUDIO", setup.GetProperty("generationConfig").GetProperty("responseModalities")[0].GetString());

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
                    bidiGenerateContentSetup = new
                    {
                        model = "models/client-owned-model",
                        generationConfig = new { responseModalities = new[] { "AUDIO" } }
                    }
                })
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task GetRealtimeToken_requires_client_owned_google_payload()
    {
        var provider = CreateProvider(new RecordingHandler(_ =>
            throw new InvalidOperationException("The upstream request must not be sent.")));

        var exception = await Assert.ThrowsAsync<ArgumentException>(() => provider.GetRealtimeToken(
            new RealtimeRequest { Model = "gemini-live" },
            CancellationToken.None));

        Assert.Contains("providerOptions.google is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRealtimeToken_surfaces_upstream_failure_body()
    {
        var provider = CreateProvider(new RecordingHandler(_ =>
            JsonResponse("""{ "error": { "message": "invalid constraints" } }""", HttpStatusCode.BadRequest)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            RequestWithGooglePayload(),
            CancellationToken.None));

        Assert.Contains("invalid constraints", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{}", "token name")]
    public async Task GetRealtimeToken_rejects_invalid_success_response(string body, string expectedMessage)
    {
        var provider = CreateProvider(new RecordingHandler(_ => JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            RequestWithGooglePayload(),
            CancellationToken.None));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRealtimeToken_rejects_invalid_client_supplied_expiry_when_response_omits_it()
    {
        var provider = CreateProvider(new RecordingHandler(_ =>
            JsonResponse("""{ "name": "auth_tokens/token" }""")));
        var request = RequestWithGooglePayload();
        request.ProviderOptions!["google"] = JsonSerializer.SerializeToElement(new
        {
            uses = 1,
            expireTime = "invalid",
            bidiGenerateContentSetup = new { model = "models/gemini-live" }
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            request,
            CancellationToken.None));

        Assert.Contains("client-supplied expireTime", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RealtimeRequest RequestWithGooglePayload()
        => new()
        {
            Model = "gemini-live",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    uses = 1,
                    expireTime = "2026-08-27T15:00:00Z",
                    bidiGenerateContentSetup = new { model = "models/gemini-live" }
                })
            }
        };

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
