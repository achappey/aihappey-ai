using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Abstractions.Http;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Tembo;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Tembo;

public class TemboProviderUnifiedTests
{
    [Fact]
    public async Task StreamUnifiedAsync_PollsAfterTaskCreateWithoutStatus_UntilPullRequestIsCreated()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.RequestUri?.AbsolutePath == "/task/create")
                return JsonResponse(CreateTaskJson("created-task", status: null, artifactStatus: null, mergedAt: null));

            if (request.RequestUri?.AbsolutePath == "/task/list")
            {
                var taskListCalls = requests.Count(message => message.RequestUri?.AbsolutePath == "/task/list");
                var includePullRequest = taskListCalls >= 2;
                return JsonResponse($$"""
                    {
                      "issues": [{{CreateTaskJson("created-task", status: null, artifactStatus: includePullRequest ? "open" : null, mergedAt: null, includePullRequest: includePullRequest)}}],
                      "meta": { "totalCount": 1, "totalPages": 1, "currentPage": 1, "pageSize": 100 }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var events = await CollectAsync(provider.StreamUnifiedAsync(CreateRequest()));

        Assert.Equal(1, requests.Count(request => request.RequestUri?.AbsolutePath == "/task/create"));
        Assert.Equal(2, requests.Count(request => request.RequestUri?.AbsolutePath == "/task/list"));
        Assert.Contains(events, streamEvent => streamEvent.Event.Type == "finish");
        Assert.Contains(events, streamEvent =>
            streamEvent.Event.Type == "tool-output-available"
            && streamEvent.Event.Data is AIToolOutputAvailableEventData { Preliminary: true });
        Assert.Contains(events, streamEvent =>
            streamEvent.Event.Type == "tool-output-available"
            && streamEvent.Event.Data is AIToolOutputAvailableEventData { Preliminary: false });

        var textDeltas = events
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(
            [
                "Test task",
                "Pull request created:\n\n- [Test PR](https://github.com/example/repo/pull/1)"
            ],
            textDeltas);

        Assert.DoesNotContain(textDeltas, text => text.Contains("running", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(textDeltas, text => text.Contains("finished with status", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StreamUnifiedAsync_WithTemboTaskFixture_EmitsTitleThenPullRequestsAndStopsBeforeMerge()
    {
        var fixtureItems = LoadTemboTaskFixture("Fixtures/tembo/typed/tembo-task.json");
        var listIndex = 1;
        var requests = new List<HttpRequestMessage>();

        var provider = CreateProvider(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.RequestUri?.AbsolutePath == "/task/create")
                return JsonResponse(fixtureItems[0]);

            if (request.RequestUri?.AbsolutePath == "/task/list")
            {
                var current = fixtureItems[Math.Min(listIndex++, fixtureItems.Count - 1)];
                return JsonResponse($$"""
                    {
                      "issues": [{{current}}],
                      "meta": { "totalCount": 1, "totalPages": 1, "currentPage": 1, "pageSize": 100 }
                    }
                    """);
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var events = await CollectAsync(provider.StreamUnifiedAsync(CreateRequest(new Dictionary<string, object?>
        {
            ["tembo"] = new Dictionary<string, object?>
            {
                ["pollIntervalSeconds"] = 1,
                ["pollTimeoutSeconds"] = 30
            }
        })));

        Assert.Equal(1, requests.Count(request => request.RequestUri?.AbsolutePath == "/task/create"));
        Assert.Equal(9, requests.Count(request => request.RequestUri?.AbsolutePath == "/task/list"));
        Assert.DoesNotContain(requests.Skip(1).Select((_, index) => fixtureItems[Math.Min(index + 1, fixtureItems.Count - 1)]), json => json.Contains("\"status\":\"merged\"", StringComparison.OrdinalIgnoreCase));

        var textDeltas = events
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(2, textDeltas.Count);
        Assert.Equal("Add privacy and terms links to cheapestinference provider catalog item", textDeltas[0]);
        Assert.Equal("Pull request created:\n\n- [Add privacy and terms links](https://github.com/achappey/aihappey-chat/pull/8)", textDeltas[1]);
        Assert.DoesNotContain(textDeltas, text => text.Contains("Tembo session", StringComparison.OrdinalIgnoreCase));

        var responseOutput = events.Single(streamEvent => streamEvent.Event.Type == "finish").Event.Output;
        var assistantMessages = responseOutput?.Items?
            .Where(item => item.Type == "message" && item.Role == "assistant")
            .Select(item => Assert.IsType<AITextContentPart>(Assert.Single(item.Content!)).Text)
            .ToList();

        Assert.Equal(textDeltas, assistantMessages);
    }

    [Fact]
    public async Task StreamUnifiedAsync_ForCompletedTaskWithoutPullRequests_EmitsOnlyTitle()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.RequestUri?.AbsolutePath == "/task/create")
                return JsonResponse(CreateTaskJson("no-pr-task", status: null, artifactStatus: null, mergedAt: null, includePullRequest: false));

            if (request.RequestUri?.AbsolutePath == "/task/list")
                return JsonResponse($$"""
                    {
                      "issues": [{{CreateTaskJson("no-pr-task", status: "completed", artifactStatus: null, mergedAt: null, includePullRequest: false)}}],
                      "meta": { "totalCount": 1, "totalPages": 1, "currentPage": 1, "pageSize": 100 }
                    }
                    """);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var events = await CollectAsync(provider.StreamUnifiedAsync(CreateRequest()));
        var textDeltas = events
            .Where(streamEvent => streamEvent.Event.Type == "text-delta")
            .Select(streamEvent => Assert.IsType<AITextDeltaEventData>(streamEvent.Event.Data).Delta)
            .ToList();

        Assert.Equal(["Test task"], textDeltas);
        Assert.Contains(events, streamEvent => streamEvent.Event.Type == "finish");
    }

    [Fact]
    public async Task StreamUnifiedAsync_EmitsPollAttemptWithoutReplacingFinalRaw_WhenListDoesNotContainTask()
    {
        var requests = new List<HttpRequestMessage>();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var provider = CreateProvider(request =>
        {
            requests.Add(CloneRequest(request));

            if (request.RequestUri?.AbsolutePath == "/task/create")
                return JsonResponse(CreateTaskJson("missing-task", status: null, artifactStatus: null, mergedAt: null));

            if (request.RequestUri?.AbsolutePath == "/task/list")
                return JsonResponse("""
                    {
                      "issues": [],
                      "meta": { "totalCount": 0, "totalPages": 1, "currentPage": 1, "pageSize": 100 }
                    }
                    """);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var request = CreateRequest(new Dictionary<string, object?>
        {
            ["tembo"] = new Dictionary<string, object?>
            {
                ["pollIntervalSeconds"] = 1,
                ["pollTimeoutSeconds"] = 30
            }
        });

        var toolOutputs = new List<AIToolOutputAvailableEventData>();

        await foreach (var streamEvent in provider.StreamUnifiedAsync(request, cancellation.Token))
        {
            if (streamEvent.Event.Data is AIToolOutputAvailableEventData output)
                toolOutputs.Add(output);

            if (toolOutputs.Count >= 2)
                break;
        }

        Assert.True(requests.Count(message => message.RequestUri?.AbsolutePath == "/task/list") >= 1);
        Assert.Equal(2, toolOutputs.Count);
        Assert.True(toolOutputs[1].Preliminary);
        var outputJson = System.Text.Json.JsonSerializer.Serialize(toolOutputs[1].Output, System.Text.Json.JsonSerializerOptions.Web);
        Assert.Contains("missing-task", outputJson);
        Assert.Contains("pollAttempt", outputJson);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_MatchesTaskListUsingDataArrayAndHtmlUrlSuffix()
    {
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/task/create")
                return JsonResponse(CreateTaskJson("html-match-task", status: null, artifactStatus: null, mergedAt: null));

            if (request.RequestUri?.AbsolutePath == "/task/list")
                return JsonResponse($$"""
                    {
                      "data": [
                        {
                          "id": "different-id",
                          "title": "Test task",
                          "prompt": "Please do the thing",
                          "status": "completed",
                          "createdAt": "2026-05-22T11:03:42.043Z",
                          "updatedAt": "2026-05-22T11:07:07.639Z",
                          "htmlUrl": "https://app.tembo.io/sessions/html-match-task"
                        }
                      ]
                    }
                    """);

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest(new Dictionary<string, object?>
        {
            ["tembo"] = new Dictionary<string, object?>
            {
                ["pollIntervalSeconds"] = 1,
                ["pollTimeoutSeconds"] = 5
            }
        }));

        Assert.Equal("completed", response.Status);
        Assert.Equal("completed", response.Metadata?["tembo.session_status"]);
        Assert.Equal("completed", response.Metadata?["tembo.session_effective_status"]);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_CapturesTaskCreateAndPollingResponsesInSingleJsonFile()
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

            var provider = CreateProvider(request =>
            {
                if (request.RequestUri?.AbsolutePath == "/task/create")
                    return JsonResponse(CreateTaskJson("captured-task", status: null, artifactStatus: null, mergedAt: null));

                if (request.RequestUri?.AbsolutePath == "/task/list")
                {
                    return JsonResponse($$"""
                        {
                          "issues": [
                            {{CreateTaskJson("other-task", status: "completed", artifactStatus: null, mergedAt: null)}},
                            {{CreateTaskJson("captured-task", status: "completed", artifactStatus: null, mergedAt: null)}}
                          ],
                          "meta": { "totalCount": 1, "totalPages": 1, "currentPage": 1, "pageSize": 100 }
                        }
                        """);
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
            });

            await provider.ExecuteUnifiedAsync(CreateRequest(new Dictionary<string, object?>
            {
                ["tembo"] = new Dictionary<string, object?>
                {
                    ["pollIntervalSeconds"] = 1,
                    ["pollTimeoutSeconds"] = 5,
                    ["backend_capture"] = ProviderBackendCaptureRequest.Create("tembo-single-task", "tembo-task-capture.json")
                }
            }));

            var captureFile = Assert.Single(Directory.GetFiles(captureRoot, "tembo-task-capture.json", SearchOption.AllDirectories));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(captureFile));
            var entries = document.RootElement.EnumerateArray().ToList();

            Assert.Equal(2, entries.Count);
            Assert.Equal("captured-task", entries[0].GetProperty("id").GetString());
            Assert.Equal("captured-task", entries[1].GetProperty("id").GetString());
            Assert.Equal("completed", entries[1].GetProperty("status").GetString());
            Assert.False(entries[0].TryGetProperty("phase", out _));
            Assert.False(entries[1].TryGetProperty("rawResponseBody", out _));
            Assert.DoesNotContain("other-task", await File.ReadAllTextAsync(captureFile));
        }
        finally
        {
            ProviderBackendCapture.Configure(previousCaptureOptions);
            TryDeleteDirectory(captureRoot);
        }
    }

    private static TemboProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        return new TemboProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static AIRequest CreateRequest(Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "tembo",
            Model = "claude-sonnet-4-6",
            Id = "request-id",
            Input = new AIInput
            {
                Text = "Please do the thing"
            },
            Metadata = metadata
        };

    private static async Task<List<AIStreamEvent>> CollectAsync(IAsyncEnumerable<AIStreamEvent> events)
    {
        var result = new List<AIStreamEvent>();
        await foreach (var item in events)
            result.Add(item);

        return result;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static string CreateTaskJson(string id, string? status, string? artifactStatus, string? mergedAt, bool? includePullRequest = null)
    {
        var statusJson = status is null ? string.Empty : $"\"status\": \"{status}\",";
        var artifactStatusJson = artifactStatus is null ? "null" : $"\"{artifactStatus}\"";
        var mergedAtJson = mergedAt is null ? "null" : $"\"{mergedAt}\"";
        var shouldIncludePullRequest = includePullRequest ?? artifactStatus is not null || mergedAt is not null;
        var pullRequestJson = shouldIncludePullRequest
            ? $$"""
                    {
                      "id": "pr-{{id}}",
                      "url": "https://github.com/example/repo/pull/1",
                      "title": "Test PR",
                      "status": {{artifactStatusJson}},
                      "mergedAt": {{mergedAtJson}},
                      "isDraft": false
                    }
                """
            : string.Empty;

        return $$"""
            {
              "id": "{{id}}",
              "metadata": {},
              "createdAt": "2026-05-22T11:03:42.043Z",
              "updatedAt": "2026-05-22T11:07:07.639Z",
              "organizationId": "org_test",
              "title": "Test task",
              "hash": "hash-{{id}}",
              "prompt": "Please do the thing",
              {{statusJson}}
              "lastQueuedAt": "2026-05-22T11:03:42.273Z",
              "lastQueuedBy": "queue",
              "htmlUrl": "https://app.tembo.io/sessions/{{id}}",
              "artifacts": [
                {
                  "id": "artifact-{{id}}",
                  "type": "PullRequest",
                  "jobId": "job-{{id}}",
                  "pullRequest": [
                    {{pullRequestJson}}
                  ]
                }
              ]
            }
            """;
    }

    private static IReadOnlyList<string> LoadTemboTaskFixture(string relativePath)
    {
        var fullPath = FixtureFileLoader.ResolveFixturePath(relativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(fullPath));
        return document.RootElement.EnumerateArray()
            .Select(item => item.GetRawText())
            .ToList();
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        => new(request.Method, request.RequestUri);

    private static string CreateTempCaptureRoot()
        => Path.Combine(Path.GetTempPath(), "aihappey-tembo-capture-tests", Guid.NewGuid().ToString("N"));

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
            => Task.FromResult(responder(request));
    }
}
