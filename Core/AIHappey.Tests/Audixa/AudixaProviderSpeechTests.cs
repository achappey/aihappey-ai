using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Audixa;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Audixa;

public sealed class AudixaProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequest_posts_v3_tts_polls_generation_and_downloads_audio()
    {
        HttpRequestMessage? capturedCreateRequest = null;
        string? capturedCreateBody = null;
        HttpRequestMessage? capturedPollRequest = null;

        var provider = CreateProvider(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v3/tts")
            {
                capturedCreateRequest = await CloneRequestAsync(request);
                capturedCreateBody = await request.Content!.ReadAsStringAsync();

                return JsonResponse(
                    """
                    {
                      "generation_id": "gen_123",
                      "status": "IN_QUEUE",
                      "input_text": "Hello from Audixa",
                      "voice_id": "am_ethan",
                      "voice_name": "Ethan",
                      "model": "advanced",
                      "tokens": 3,
                      "dollar_cost": 0.000003,
                      "method": null,
                      "created_at": "2025-02-05T12:00:00Z"
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v3/tts?generation_id=gen_123")
            {
                capturedPollRequest = await CloneRequestAsync(request);

                return JsonResponse(
                    """
                    {
                      "generation_id": "gen_123",
                      "status": "COMPLETED",
                      "input_text": "Hello from Audixa",
                      "voice_id": "am_ethan",
                      "voice_name": "Ethan",
                      "model": "advanced",
                      "tokens": 3,
                      "dollar_cost": 0.000003,
                      "method": "API_WALLET",
                      "audio_url": "https://cdn.audixa.ai/audio/gen_123.mp3",
                      "created_at": "2025-02-05T12:00:00Z",
                      "started_at": "2025-02-05T12:00:01Z",
                      "completed_at": "2025-02-05T12:00:02Z"
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == "https://cdn.audixa.ai/audio/gen_123.mp3")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Encoding.UTF8.GetBytes("audixa-audio"))
                };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
                return response;
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "audixa/advanced/am_ethan",
            Text = "Hello from Audixa",
            Voice = "ignored_voice",
            OutputFormat = "mp3",
            Speed = 1.25f,
            Language = "en",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["audixa"] = JsonSerializer.SerializeToElement(new
                {
                    cfg_weight = 3.5,
                    exaggeration = 0.7,
                    custom_passthrough = true
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedCreateRequest);
        Assert.Equal("/v3/tts", capturedCreateRequest!.RequestUri?.AbsolutePath);
        Assert.Equal("test-key", capturedCreateRequest.Headers.GetValues("x-api-key").Single());
        Assert.NotNull(capturedPollRequest);

        using var createDocument = JsonDocument.Parse(capturedCreateBody!);
        var payload = createDocument.RootElement;
        Assert.Equal("Hello from Audixa", payload.GetProperty("text").GetString());
        Assert.Equal("am_ethan", payload.GetProperty("voice_id").GetString());
        Assert.Equal("advanced", payload.GetProperty("model").GetString());
        Assert.Equal("mp3", payload.GetProperty("audio_format").GetString());
        Assert.Equal("en", payload.GetProperty("language_code").GetString());
        Assert.Equal(1.25f, payload.GetProperty("speed").GetSingle());
        Assert.Equal(3.5, payload.GetProperty("cfg_weight").GetDouble());
        Assert.Equal(0.7, payload.GetProperty("exaggeration").GetDouble());
        Assert.True(payload.GetProperty("custom_passthrough").GetBoolean());

        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("audixa-audio")), response.Audio.Base64);
        Assert.Equal("audio/mpeg", response.Audio.MimeType);
        Assert.Equal("mp3", response.Audio.Format);
        Assert.Contains(response.Warnings, warning => JsonSerializer.Serialize(warning).Contains("voice is derived from model id", StringComparison.Ordinal));
        Assert.NotNull(response.Request?.Body);

        Assert.NotNull(response.ProviderMetadata);
        Assert.Equal("gen_123", response.ProviderMetadata!["generation_id"].GetString());
        Assert.Equal("COMPLETED", response.ProviderMetadata["status"].GetString());
        Assert.Equal("am_ethan", response.ProviderMetadata["voice_id"].GetString());
        Assert.Equal("advanced", response.ProviderMetadata["model"].GetString());
        Assert.Equal("mp3", response.ProviderMetadata["audio_format"].GetString());
        Assert.Equal("API_WALLET", response.ProviderMetadata["method"].GetString());
        Assert.Equal(3, response.ProviderMetadata["tokens"].GetInt32());
    }

    [Fact]
    public async Task SpeechRequest_throws_when_v3_generation_fails()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v3/tts")
            {
                return JsonResponse(
                    """
                    {
                      "generation_id": "gen_failed",
                      "status": "IN_QUEUE",
                      "input_text": "Hello",
                      "voice_id": "am_ethan",
                      "model": "base"
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.PathAndQuery == "/v3/tts?generation_id=gen_failed")
            {
                return JsonResponse(
                    """
                    {
                      "generation_id": "gen_failed",
                      "status": "FAILED",
                      "error_message": "Voice not found"
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "base",
            Text = "Hello",
            Voice = "am_ethan"
        }));

        Assert.Contains("Voice not found", ex.Message, StringComparison.Ordinal);
    }

    private static AudixaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static AudixaProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.audixa.ai/")
        };

        return new AudixaProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(httpClient));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            var content = await request.Content.ReadAsStringAsync();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "audixa" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
