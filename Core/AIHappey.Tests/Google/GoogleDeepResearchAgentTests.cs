using System.Net;
using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleDeepResearchAgentTests
{
    [Fact]
    public void DeepResearchModelIsNormalizedToAgentRequest()
    {
        var request = new InteractionRequest
        {
            Model = "google/deep-research-preview-04-2026",
            Stream = true,
            Store = false,
            GenerationConfig = new InteractionGenerationConfig
            {
                Temperature = 0.2f
            }
        };

        var normalized = InvokeNormalize(request, out var agent);

        Assert.True(normalized);
        Assert.Equal("deep-research-preview-04-2026", agent);
        Assert.Null(request.Model);
        Assert.Equal("deep-research-preview-04-2026", request.Agent);
        Assert.True(request.Background);
        Assert.True(request.Store);
        Assert.Null(request.Stream);
        Assert.Null(request.GenerationConfig);
    }

    [Fact]
    public void DeepResearchModelIsNormalizedToNativeStreamingAgentRequest()
    {
        var request = new InteractionRequest
        {
            Model = "google/deep-research-preview-04-2026",
            Stream = false,
            Store = false,
            GenerationConfig = new InteractionGenerationConfig
            {
                Temperature = 0.2f
            }
        };

        var normalized = InvokeNormalize(request, out var agent, stream: true);

        Assert.True(normalized);
        Assert.Equal("deep-research-preview-04-2026", agent);
        Assert.Null(request.Model);
        Assert.Equal("deep-research-preview-04-2026", request.Agent);
        Assert.True(request.Background);
        Assert.True(request.Store);
        Assert.True(request.Stream);
        var config = Assert.IsType<InteractionDeepResearchAgentConfig>(request.AgentConfig);
        Assert.Equal("auto", config.ThinkingSummaries);
        Assert.Null(request.GenerationConfig);
    }

    [Fact]
    public async Task NonStreamingDeepResearchPollsAndDeletesStoredInteraction()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    id = "interaction-1",
                    agent = "deep-research-preview-04-2026",
                    status = "in_progress"
                })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    id = "interaction-1",
                    agent = "deep-research-preview-04-2026",
                    status = "completed",
                    outputs = new[]
                    {
                        new
                        {
                            type = "text",
                            text = "final report"
                        }
                    }
                })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);

        var provider = CreateProvider(handler);
        var result = await provider.GetInteraction(new InteractionRequest
        {
            Model = "deep-research-preview-04-2026",
            Input = new InteractionsInput("research this")
        });

        Assert.Equal("completed", result.Status);
        Assert.Equal("deep-research-preview-04-2026", result.Agent);
        Assert.Collection(handler.Requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.EndsWith("/v1beta/interactions", create.RequestUri!.ToString());
                using var doc = JsonDocument.Parse(create.Body!);
                Assert.False(doc.RootElement.TryGetProperty("model", out _));
                Assert.Equal("deep-research-preview-04-2026", doc.RootElement.GetProperty("agent").GetString());
                Assert.True(doc.RootElement.GetProperty("background").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("store").GetBoolean());
                Assert.False(doc.RootElement.TryGetProperty("stream", out _));
                Assert.False(doc.RootElement.TryGetProperty("generation_config", out _));
            },
            get =>
            {
                Assert.Equal(HttpMethod.Get, get.Method);
                Assert.EndsWith("/v1beta/interactions/interaction-1", get.RequestUri!.ToString());
            },
            delete =>
            {
                Assert.Equal(HttpMethod.Delete, delete.Method);
                Assert.EndsWith("/v1beta/interactions/interaction-1", delete.RequestUri!.ToString());
            });
    }

    [Fact]
    public async Task StreamingDeepResearchUsesNativeStreamAndDeletesStoredInteraction()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = SseContent(
                    new
                    {
                        event_type = "interaction.start",
                        event_id = "event-1",
                        interaction = new
                        {
                            id = "interaction-stream-1",
                            agent = "deep-research-preview-04-2026",
                            status = "in_progress"
                        }
                    },
                    new
                    {
                        event_type = "content.start",
                        event_id = "event-2",
                        index = 0,
                        content = new
                        {
                            type = "text"
                        }
                    },
                    new
                    {
                        event_type = "content.delta",
                        event_id = "event-3",
                        index = 0,
                        delta = new
                        {
                            type = "text",
                            text = "streamed final report"
                        }
                    },
                    new
                    {
                        event_type = "content.stop",
                        event_id = "event-4",
                        index = 0
                    },
                    new
                    {
                        event_type = "interaction.complete",
                        event_id = "event-5",
                        interaction = new
                        {
                            id = "interaction-stream-1",
                            agent = "deep-research-preview-04-2026",
                            status = "completed"
                        }
                    })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);

        var provider = CreateProvider(handler);
        var parts = new List<InteractionStreamEventPart>();
        await foreach (var part in provider.GetInteractions(new InteractionRequest
                       {
                           Model = "deep-research-preview-04-2026",
                           Input = new InteractionsInput("research this"),
                           Stream = true
                       }))
        {
            parts.Add(part);
        }

        Assert.Contains(parts, part => part is InteractionStartEvent);
        Assert.Contains(parts, part => part is InteractionContentStartEvent { Content: InteractionTextContent });
        Assert.Contains(parts, part => part is InteractionContentDeltaEvent { Delta.Type: "text", Delta.Text: "streamed final report" });
        Assert.Contains(parts, part => part is InteractionContentStopEvent);
        Assert.Contains(parts, part => part is InteractionCompleteEvent { Interaction.Status: "completed" });

        Assert.Collection(handler.Requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                using var doc = JsonDocument.Parse(create.Body!);
                Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("background").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("store").GetBoolean());
                Assert.Equal("auto", doc.RootElement.GetProperty("agent_config").GetProperty("thinking_summaries").GetString());
                Assert.False(doc.RootElement.TryGetProperty("generation_config", out _));
            },
            delete => Assert.Equal(HttpMethod.Delete, delete.Method));

        var unifiedTypes = parts.SelectMany(part => part.ToUnifiedStreamEvent("google")).Select(part => part.Event.Type).ToList();
        Assert.Contains("text-start", unifiedTypes);
        Assert.Contains("text-delta", unifiedTypes);
        Assert.Contains("text-end", unifiedTypes);
        Assert.Contains("finish", unifiedTypes);

        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    [Fact]
    public async Task StreamingDeepResearchEmitsNativeErrorAndDeletesStoredInteraction()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = SseContent(
                    new
                    {
                        event_type = "interaction.start",
                        event_id = "event-1",
                        interaction = new
                        {
                            id = "interaction-failed-1",
                            agent = "deep-research-preview-04-2026",
                            status = "in_progress"
                        }
                    },
                    new
                    {
                        event_type = "error",
                        event_id = "event-2",
                        error = new
                        {
                            code = "failed",
                            message = "Research failed"
                        }
                    })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);

        var provider = CreateProvider(handler);
        var parts = new List<InteractionStreamEventPart>();
        await foreach (var part in provider.GetInteractions(new InteractionRequest
                       {
                           Model = "deep-research-preview-04-2026",
                           Input = new InteractionsInput("research this")
                       }))
        {
            parts.Add(part);
        }

        var error = Assert.Single(parts.OfType<InteractionErrorEvent>());
        Assert.Equal("failed", error.Error?.Code);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Delete);
    }

    private static bool InvokeNormalize(InteractionRequest request, out string agent, bool stream = false)
    {
        var method = typeof(GoogleAIProvider).GetMethod(
            "TryNormalizeGoogleAgentRequest",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var parameters = new object?[] { request, null, stream };
        var result = (bool)method.Invoke(null, parameters)!;
        agent = (string)parameters[1]!;
        return result;
    }

    private static GoogleAIProvider CreateProvider(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new GoogleAIProvider(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(httpClient));
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json");

    private static StringContent SseContent(params object[] events)
    {
        var lines = events
            .Select(e => $"data: {JsonSerializer.Serialize(e, JsonSerializerOptions.Web)}")
            .Append("data: [DONE]");

        return new StringContent(string.Join("\n\n", lines), System.Text.Encoding.UTF8, "text/event-stream");
    }

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(IEnumerable<HttpResponseMessage> queuedResponses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(queuedResponses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            Assert.True(responses.TryDequeue(out var response), $"No response queued for {request.Method} {request.RequestUri}.");
            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
