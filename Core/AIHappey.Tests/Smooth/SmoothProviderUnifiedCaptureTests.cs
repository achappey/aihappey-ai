using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Smooth;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Smooth;

public class SmoothProviderUnifiedCaptureTests
{
    public static TheoryData<string> CaptureMetadataKeys =>
    [
        "capture",
        "backend_capture"
    ];

    [Theory]
    [MemberData(nameof(CaptureMetadataKeys))]
    public async Task ExecuteUnifiedAsync_CapturesTaskCreateAndEveryPollInOrder(string captureMetadataKey)
    {
        var captureRoot = CreateTempCaptureRoot();
        var previousCaptureOptions = ProviderBackendCapture.Current;

        try
        {
            ProviderBackendCapture.Configure(new ProviderBackendCaptureOptions
            {
                Enabled = true,
                DevelopmentOnly = false,
                RootDirectory = captureRoot
            });

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

            var response = await provider.ExecuteUnifiedAsync(CreateRequest(
                captureMetadataKey,
                ProviderBackendCaptureRequest.Create("smooth-tests", $"{captureMetadataKey}.json")));

            Assert.Equal("completed", response.Status);
            Assert.Equal(2, pollCount);

            var captureFile = Assert.Single(Directory.GetFiles(captureRoot, $"{captureMetadataKey}.json", SearchOption.AllDirectories));
            var captureText = await File.ReadAllTextAsync(captureFile);
            using var document = JsonDocument.Parse(captureText);
            var entries = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(3, entries.Count);
            Assert.Equal("created", entries[0].GetProperty("r").GetProperty("output").GetString());
            Assert.Equal("poll-1", entries[1].GetProperty("r").GetProperty("output").GetString());
            Assert.Equal("final-result", entries[2].GetProperty("r").GetProperty("output").GetString());
            Assert.Equal("done", entries[2].GetProperty("r").GetProperty("status").GetString());
            Assert.DoesNotContain("unrelated", captureText);
        }
        finally
        {
            ProviderBackendCapture.Configure(previousCaptureOptions);
            TryDeleteDirectory(captureRoot);
        }
    }

    private static SmoothProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        return new SmoothProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static AIRequest CreateRequest(string captureMetadataKey, ProviderBackendCaptureRequest capture)
        => new()
        {
            ProviderId = "smooth",
            Model = "smooth/smooth-agent",
            Input = new AIInput { Text = "Complete this task" },
            Metadata = new Dictionary<string, object?>
            {
                ["smooth"] = new Dictionary<string, object?>
                {
                    ["poll_interval_ms"] = 1,
                    [captureMetadataKey] = capture
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

    private static string CreateTempCaptureRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "aihappey-smooth-capture-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

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
