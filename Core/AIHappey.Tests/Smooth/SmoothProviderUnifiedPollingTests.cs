using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Smooth;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Smooth;

public class SmoothProviderUnifiedPollingTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_PollsUntilDoneAndPreservesFinalResponse()
    {
        var pollCount = 0;
        var provider = CreateProvider(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/api/v1/task")
                return JsonResponse(TaskEnvelope("task-1", "running", "created"));

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/api/v1/task/task-1")
            {
                pollCount++;
                return JsonResponse(pollCount == 1
                    ? TaskEnvelope("task-1", "running", "poll-1")
                    : TaskEnvelope("task-1", "done", "final-result"));
            }

            return JsonResponse("{\"r\":{\"unrelated\":true}}", HttpStatusCode.NotFound);
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest());

        Assert.Equal("completed", response.Status);
        Assert.Equal(2, pollCount);
        var raw = Assert.IsType<JsonElement>(response.Metadata!["smooth_raw_response"]);
        Assert.Equal("final-result", raw.GetProperty("r").GetProperty("output").GetString());
        Assert.Equal("done", raw.GetProperty("r").GetProperty("status").GetString());
    }

    private static SmoothProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        return new SmoothProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static AIRequest CreateRequest()
        => new()
        {
            ProviderId = "smooth",
            Model = "smooth/smooth-agent",
            Input = new AIInput { Text = "Complete this task" },
            Metadata = new Dictionary<string, object?>
            {
                ["smooth"] = new Dictionary<string, object?>
                {
                    ["poll_interval_ms"] = 1
                }
            }
        };

    private static string TaskEnvelope(string id, string status, string output)
        => JsonSerializer.Serialize(new
        {
            r = new
            {
                id,
                status,
                output,
                credits_used = 1,
                events = Array.Empty<object>()
            }
        });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
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
        {
            var response = responder(request);
            response.RequestMessage ??= request;
            return Task.FromResult(response);
        }
    }
}
