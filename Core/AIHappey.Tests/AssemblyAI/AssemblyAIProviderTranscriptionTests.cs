using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AssemblyAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AssemblyAI;

public sealed class AssemblyAIProviderTranscriptionTests
{
    [Theory]
    [InlineData("assemblyai/universal-3-5-pro", "universal-3-5-pro")]
    [InlineData("universal-3-5-pro", "universal-3-5-pro")]
    [InlineData("universal-2", "universal-2")]
    public async Task Transcription_request_sends_native_speech_model_and_preserves_model_id(
        string model,
        string expectedSpeechModel)
    {
        string? createTranscriptBody = null;
        var provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v2/upload")
                return Json("""{"upload_url":"https://audio.example/audio.wav"}""");
            if (request.Method == HttpMethod.Post && path == "/v2/transcript")
            {
                createTranscriptBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                return Json("""{"id":"transcript_123"}""");
            }
            if (request.Method == HttpMethod.Get && path == "/v2/transcript/transcript_123")
                return Json("""{"status":"completed","text":"hello","audio_duration":2}""");
            if (request.Method == HttpMethod.Delete && path == "/v2/transcript/transcript_123")
                return Json("{}");

            return Json("{}", HttpStatusCode.NotFound);
        });

        var request = CreateRequest();
        request.Model = model;
        var response = await provider.TranscriptionRequest(request);

        using var body = JsonDocument.Parse(createTranscriptBody!);
        Assert.Equal(
            expectedSpeechModel,
            body.RootElement.GetProperty("speech_models")[0].GetString());
        Assert.Equal(model, response.Response.ModelId);
        Assert.Equal("hello", response.Text);
    }

    [Fact]
    public async Task Transcription_request_uses_independent_token_for_cleanup_after_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var cleanupAttempted = false;
        var provider = CreateProvider((request, requestToken) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v2/upload")
                return Json("""{"upload_url":"https://audio.example/audio.wav"}""");
            if (request.Method == HttpMethod.Post && path == "/v2/transcript")
                return Json("""{"id":"transcript_123"}""");
            if (request.Method == HttpMethod.Get && path == "/v2/transcript/transcript_123")
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            if (request.Method == HttpMethod.Delete && path == "/v2/transcript/transcript_123")
            {
                cleanupAttempted = requestToken.CanBeCanceled
                    && requestToken != cancellation.Token
                    && !requestToken.IsCancellationRequested;
                return Json("{}", HttpStatusCode.InternalServerError);
            }

            return Json("{}", HttpStatusCode.NotFound);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.TranscriptionRequest(CreateRequest(), cancellation.Token));

        Assert.True(cleanupAttempted);
    }

    [Fact]
    public async Task Transcription_request_returns_success_with_warning_when_cleanup_fails()
    {
        var provider = CreateProvider((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v2/upload")
                return Json("""{"upload_url":"https://audio.example/audio.wav"}""");
            if (request.Method == HttpMethod.Post && path == "/v2/transcript")
                return Json("""{"id":"transcript_123"}""");
            if (request.Method == HttpMethod.Get && path == "/v2/transcript/transcript_123")
                return Json("""{"status":"completed","text":"hello","audio_duration":2}""");
            if (request.Method == HttpMethod.Delete && path == "/v2/transcript/transcript_123")
                return Json("{}", HttpStatusCode.InternalServerError);

            return Json("{}", HttpStatusCode.NotFound);
        });

        var response = await provider.TranscriptionRequest(CreateRequest());

        Assert.Equal("hello", response.Text);
        var warning = Assert.Single(response.Warnings);
        var warningJson = JsonSerializer.Serialize(warning);
        Assert.Contains("cleanup_failed", warningJson, StringComparison.Ordinal);
        Assert.Contains("transcript_123", warningJson, StringComparison.Ordinal);
    }

    private static TranscriptionRequest CreateRequest()
        => new()
        {
            Model = "universal-2",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        };

    private static AssemblyAIProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new StaticResponseHandler(responder))
        {
            BaseAddress = new Uri("https://api.assemblyai.com/")
        };

        return new AssemblyAIProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request, cancellationToken));
    }
}
