using System.Net;
using System.Text;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Gladia;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Gladia;

public sealed class GladiaProviderTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_deletes_job_when_polling_fails()
    {
        var requests = new List<(HttpMethod Method, string Path)>();
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add((request.Method, path));

            return (request.Method, path) switch
            {
                (var method, "/v2/upload") when method == HttpMethod.Post =>
                    Json("""{"audio_url":"https://audio.example/audio.wav"}"""),
                (var method, "/v2/pre-recorded") when method == HttpMethod.Post =>
                    Json("""{"id":"job_123"}"""),
                (var method, "/v2/pre-recorded/job_123") when method == HttpMethod.Get =>
                    Json("""{"status":"error","error_code":"transcription_failed"}"""),
                (var method, "/v2/pre-recorded/job_123") when method == HttpMethod.Delete =>
                    Json("{}"),
                _ => Json("{}", HttpStatusCode.NotFound)
            };
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.TranscriptionRequest(new TranscriptionRequest
            {
                Model = "solaria-1",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
                MediaType = "audio/wav"
            }));

        Assert.Contains("Gladia pre-recorded job failed", exception.Message, StringComparison.Ordinal);
        Assert.Contains((HttpMethod.Delete, "/v2/pre-recorded/job_123"), requests);
    }

    private static GladiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(new HttpClient(new StaticResponseHandler(responder))));

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

    private sealed class StaticResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
