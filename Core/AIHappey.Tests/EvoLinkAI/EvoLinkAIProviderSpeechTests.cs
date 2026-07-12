using System.Net;
using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.EvoLinkAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.EvoLinkAI;

public sealed class EvoLinkAIProviderSpeechTests
{
    [Fact]
    public async Task SpeechRequest_merges_provider_options_polls_task_and_downloads_audio()
    {
        var requestedPath = string.Empty;
        var requestJson = string.Empty;
        var expectedBytes = Encoding.UTF8.GetBytes("audio-bytes");

        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                requestedPath = request.RequestUri?.PathAndQuery ?? string.Empty;
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;

                return JsonResponse("""
                {"created":1775200000,"id":"task-unified-1775200000-abcd1234","model":"doubao-seed-audio-1-0","object":"audio.generation.task","progress":0,"status":"pending","type":"audio"}
                """);
            }

            if (request.Method == HttpMethod.Get
                && string.Equals(request.RequestUri?.PathAndQuery, "/v1/tasks/task-unified-1775200000-abcd1234", StringComparison.Ordinal))
            {
                return JsonResponse("""
                {"created":1775200000,"id":"task-unified-1775200000-abcd1234","model":"doubao-seed-audio-1-0","object":"audio.generation.task","progress":100,"status":"completed","results":["https://example.test/audio/generated/test.mp3"],"type":"audio"}
                """);
            }

            if (string.Equals(request.RequestUri?.AbsoluteUri, "https://example.test/audio/generated/test.mp3", StringComparison.Ordinal))
            {
                var content = new ByteArrayContent(expectedBytes);
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/mpeg");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            return Unexpected(request);
        });

        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "doubao-seed-audio-1-0",
            Text = "Welcome to EvoLinkAI speech.",
            Voice = "zh_female_vv_uranus_bigtts",
            OutputFormat = "mp3",
            Speed = 1.25f,
            ProviderOptions = ProviderOptions(new
            {
                sample_rate = 24000,
                loudness_rate = 0.85,
                pitch_rate = 0,
                poll_interval_seconds = 1,
                poll_max_attempts = 1
            })
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("/v1/audios/generations", requestedPath);
        Assert.Equal("doubao-seed-audio-1-0", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("Welcome to EvoLinkAI speech.", payload.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("mp3", payload.RootElement.GetProperty("format").GetString());
        Assert.Equal(24000, payload.RootElement.GetProperty("sample_rate").GetInt32());
        Assert.Equal("zh_female_vv_uranus_bigtts", payload.RootElement.GetProperty("audio_references")[0].GetString());
        Assert.False(payload.RootElement.TryGetProperty("poll_interval_seconds", out _));

        Assert.Equal(Convert.ToBase64String(expectedBytes), result.Audio.Base64);
        Assert.Equal("audio/mpeg", result.Audio.MimeType);
        Assert.Equal("mp3", result.Audio.Format);
        Assert.Equal("evolinkai/doubao-seed-audio-1-0", result.Response.ModelId);

        var metadata = result.ProviderMetadata?["evolinkai"];
        Assert.NotNull(metadata);
        Assert.Equal("task-unified-1775200000-abcd1234", metadata.Value.GetProperty("taskId").GetString());
        Assert.Equal("completed", metadata.Value.GetProperty("status").GetString());
        Assert.True(metadata.Value.TryGetProperty("providerOptions", out _));
    }

    [Fact]
    public async Task SpeechRequest_qwen_maps_voice_and_language_type()
    {
        var requestJson = string.Empty;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                requestJson = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult() ?? string.Empty;
                return JsonResponse("""
                {"id":"task-unified-qwen","model":"qwen3-tts-vd","status":"completed","results":["data:audio/wav;base64,YXVkaW8="]}
                """);
            }

            return Unexpected(request);
        });

        var result = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "qwen3-tts-vd",
            Text = "Good evening, listeners.",
            Voice = "qwen-tts-vd-announcer-voice-20260402-a1b2",
            Language = "English"
        });

        using var payload = JsonDocument.Parse(requestJson);

        Assert.Equal("qwen-tts-vd-announcer-voice-20260402-a1b2", payload.RootElement.GetProperty("voice").GetString());
        Assert.Equal("English", payload.RootElement.GetProperty("language_type").GetString());
        Assert.False(payload.RootElement.TryGetProperty("audio_references", out _));
        Assert.Equal("YXVkaW8=", result.Audio.Base64);
        Assert.Equal("audio/wav", result.Audio.MimeType);
    }

    [Fact]
    public async Task SpeechRequest_throws_when_task_fails()
    {
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                return JsonResponse("""
                {"id":"task-unified-failed","model":"suno-v5-beta","status":"pending"}
                """);
            }

            if (request.Method == HttpMethod.Get
                && string.Equals(request.RequestUri?.PathAndQuery, "/v1/tasks/task-unified-failed", StringComparison.Ordinal))
            {
                return JsonResponse("""
                {"id":"task-unified-failed","model":"suno-v5-beta","status":"failed","error":{"code":"content_policy_violation","message":"Content policy violation","type":"task_error"}}
                """);
            }

            return Unexpected(request);
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "suno-v5-beta",
            Text = "A cheerful summer pop song.",
            ProviderOptions = ProviderOptions(new
            {
                custom_mode = false,
                instrumental = false,
                poll_interval_seconds = 1,
                poll_max_attempts = 1
            })
        }));

        Assert.Contains("Content policy violation", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static EvoLinkAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler));
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new EvoLinkAIProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static Dictionary<string, JsonElement> ProviderOptions(object metadata)
        => new()
        {
            ["evolinkai"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
        };

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

    private static HttpResponseMessage Unexpected(HttpRequestMessage request)
        => new(HttpStatusCode.NotFound)
        {
            Content = new StringContent($"unexpected request: {request.Method} {request.RequestUri}")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
