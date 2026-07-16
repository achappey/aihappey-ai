using System.Net;
using System.Text;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Audixa;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Audixa;

public sealed class AudixaProviderSpeechTests
{
   
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
