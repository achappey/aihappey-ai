using System.Net;
using System.Reflection;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
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
    public void AntigravityModelIsNormalizedToAgentRequestWithDefaultEnvironment()
    {
        var request = new InteractionRequest
        {
            Model = "google/antigravity-preview-05-2026",
            Stream = true,
            Store = false,
            Background = true,
            GenerationConfig = new InteractionGenerationConfig
            {

            },
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["generation_config"] = JsonSerializer.SerializeToElement(new { temperature = 0.2f }, JsonSerializerOptions.Web),
                ["background"] = JsonSerializer.SerializeToElement(true, JsonSerializerOptions.Web)
            }
        };

        var normalized = InvokeNormalize(request, out var agent);

        Assert.True(normalized);
        Assert.Equal("antigravity-preview-05-2026", agent);
        Assert.Null(request.Model);
        Assert.Equal("antigravity-preview-05-2026", request.Agent);
        Assert.Null(request.Background);
        Assert.True(request.Store);
        Assert.Null(request.Stream);
        Assert.Null(request.GenerationConfig);
        Assert.Null(request.AgentConfig);
        Assert.Equal("remote", request.AdditionalProperties!["environment"].GetString());
        Assert.False(request.AdditionalProperties.TryGetValue("generation_config", out _));
        Assert.False(request.AdditionalProperties.TryGetValue("background", out _));
    }

    [Fact]
    public void AntigravityModelPreservesExplicitEnvironmentOverrideAndSupportsNativeStreaming()
    {
        var request = new InteractionRequest
        {
            Model = "models/antigravity-preview-05-2026",
            Stream = false,
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["environment"] = JsonSerializer.SerializeToElement("env_abc123", JsonSerializerOptions.Web)
            }
        };

        var normalized = InvokeNormalize(request, out var agent, stream: true);

        Assert.True(normalized);
        Assert.Equal("antigravity-preview-05-2026", agent);
        Assert.Null(request.Model);
        Assert.Equal("antigravity-preview-05-2026", request.Agent);
        Assert.Null(request.Background);
        Assert.True(request.Store);
        Assert.True(request.Stream);
        Assert.Null(request.AgentConfig);
        Assert.Equal("env_abc123", request.AdditionalProperties!["environment"].GetString());
    }

    [Theory]
    [InlineData("usr/local/lib/python3.12/dist-packages/fontTools/misc/xmlReader.py", true)]
    [InlineData("./usr/local/lib/python3.12/dist-packages/fontTools/misc/xmlReader.py", true)]
    [InlineData("mind-motion-poster/mind_motion_day.pdf", false)]
    [InlineData("mind-motion-poster/mind_motion_day.png", false)]
    public void GoogleAgentArchivePathSkipPolicyMatchesUsrLocalPrefixRule(string path, bool expected)
    {
        var actual = InvokeShouldSkipArchivePath(path);
        Assert.Equal(expected, actual);
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
                    steps = new[]
                    {
                        new
                        {
                            type = "model_output",
                            content = new[]
                            {
                                new { type = "text", text = "final report" }
                            }
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
                        event_type = "interaction.created",
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
                        event_type = "step.start",
                        event_id = "event-2",
                        index = 0,
                        step = new
                        {
                            type = "model_output",
                            content = new[] { new { type = "text", text = "" } }
                        }
                    },
                    new
                    {
                        event_type = "step.delta",
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
                        event_type = "step.stop",
                        event_id = "event-4",
                        index = 0,
                        status = "done"
                    },
                    new
                    {
                        event_type = "interaction.completed",
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

        Assert.Contains(parts, part => part is InteractionCreatedEvent);
        Assert.Contains(parts, part => part is InteractionStepStartEvent { Step: InteractionModelOutputStep });
        Assert.Contains(parts, part => part is InteractionStepDeltaEvent { Delta.Type: "text", Delta.Text: "streamed final report" });
        Assert.Contains(parts, part => part is InteractionStepStopEvent);
        Assert.Contains(parts, part => part is InteractionCompletedEvent { Interaction.Status: "completed" });

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
    public async Task GenericInteractionStreamPreservesSingleGoogleModelPrefixInFinishMetadata()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = SseContent(
                    new
                    {
                        event_type = "interaction.created",
                        event_id = "event-1",
                        interaction = new
                        {
                            id = "interaction-gemini-stream-1",
                            model = "google/gemini-3.5-flash",
                            status = "in_progress"
                        }
                    },
                    new
                    {
                        event_type = "interaction.completed",
                        event_id = "event-2",
                        interaction = new
                        {
                            id = "interaction-gemini-stream-1",
                            model = "google/gemini-3.5-flash",
                            status = "completed"
                        }
                    })
            }
        ]);

        var provider = CreateProvider(handler);
        var parts = new List<InteractionStreamEventPart>();
        await foreach (var part in provider.GetInteractions(new InteractionRequest
        {
            Model = "gemini-3.5-flash",
            Input = new InteractionsInput("test"),
            Stream = true
        }))
        {
            parts.Add(part);
        }

        Assert.Contains(parts, part => part is InteractionCreatedEvent { Interaction.Model: "google/gemini-3.5-flash" });
        Assert.Contains(parts, part => part is InteractionCompletedEvent { Interaction.Model: "google/gemini-3.5-flash" });

        var finishPart = Assert.IsType<FinishUIPart>(parts
            .SelectMany(part => part.ToUnifiedStreamEvent("google"))
            .Where(streamEvent => streamEvent.Event.Type == "finish")
            .SelectMany(streamEvent => streamEvent.Event.ToUIMessagePart("google"))
            .Single());

        Assert.Equal("google/gemini-3.5-flash", finishPart.MessageMetadata?.Model);
        Assert.DoesNotContain("google/google/", finishPart.MessageMetadata?.Model, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GenericInteractionRequestPreservesSingleGoogleModelPrefix()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    id = "interaction-gemini-1",
                    model = "google/gemini-3.5-flash",
                    status = "completed"
                })
            }
        ]);

        var provider = CreateProvider(handler);
        var result = await provider.GetInteraction(new InteractionRequest
        {
            Model = "gemini-3.5-flash",
            Input = new InteractionsInput("test")
        });

        Assert.Equal("google/gemini-3.5-flash", result.Model);
        Assert.DoesNotContain("google/google/", result.Model, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GoogleFinishCostEnrichmentPreservesSingleGoogleModelPrefixInUiMetadata()
    {
        var provider = CreateProvider(new RecordingHandler([]));
        var timestamp = DateTimeOffset.Parse("2026-06-20T15:00:00+00:00");
        var interaction = new Interaction
        {
            Id = "interaction-gemini-finish-1",
            Model = "google/gemini-3.5-flash",
            Status = "completed",
            Usage = new InteractionUsage
            {
                TotalInputTokens = 10,
                TotalOutputTokens = 5,
                TotalTokens = 15
            }
        };

        var finishEvent = new AIStreamEvent
        {
            ProviderId = "google",
            Event = new AIEventEnvelope
            {
                Type = "finish",
                Id = interaction.Id,
                Timestamp = timestamp,
                Data = new AIFinishEventData
                {
                    FinishReason = "stop",
                    Model = "google/gemini-3.5-flash",
                    CompletedAt = timestamp,
                    InputTokens = 10,
                    OutputTokens = 5,
                    TotalTokens = 15,
                    Response = interaction,
                    MessageMetadata = AIFinishMessageMetadata.Create(
                        model: "google/gemini-3.5-flash",
                        timestamp: timestamp,
                        inputTokens: 10,
                        outputTokens: 5,
                        totalTokens: 15)
                }
            }
        };

        var enrichedEvent = InvokeMarkGoogleAgentUnifiedToolEventProviderExecuted(provider, finishEvent);
        var finishPart = Assert.IsType<FinishUIPart>(enrichedEvent.Event.ToUIMessagePart("google").Single());

        Assert.Equal("google/gemini-3.5-flash", finishPart.MessageMetadata?.Model);
        Assert.DoesNotContain("google/google/", finishPart.MessageMetadata?.Model, StringComparison.OrdinalIgnoreCase);
        Assert.True(finishPart.MessageMetadata?.Gateway?.Cost > 0);
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
                        event_type = "interaction.created",
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

    [Fact]
    public async Task NonStreamingAntigravityPollsAndDeletesStoredInteraction()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    id = "interaction-antigravity-1",
                    agent = "antigravity-preview-05-2026",
                    status = "in_progress"
                })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent(new
                {
                    id = "interaction-antigravity-1",
                    agent = "antigravity-preview-05-2026",
                    status = "completed",
                    steps = new[]
                    {
                        new
                        {
                            type = "model_output",
                            content = new[]
                            {
                                new { type = "text", text = "agent output" }
                            }
                        }
                    }
                })
            },
            new HttpResponseMessage(HttpStatusCode.OK)
        ]);

        var provider = CreateProvider(handler);
        var result = await provider.GetInteraction(new InteractionRequest
        {
            Model = "antigravity-preview-05-2026",
            Input = new InteractionsInput("work in the sandbox"),
            Background = true,
            GenerationConfig = new InteractionGenerationConfig
            {
            }
        });

        Assert.Equal("completed", result.Status);
        Assert.Equal("antigravity-preview-05-2026", result.Agent);
        Assert.Collection(handler.Requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                Assert.EndsWith("/v1beta/interactions", create.RequestUri!.ToString());
                using var doc = JsonDocument.Parse(create.Body!);
                Assert.False(doc.RootElement.TryGetProperty("model", out _));
                Assert.Equal("antigravity-preview-05-2026", doc.RootElement.GetProperty("agent").GetString());
                Assert.Equal("remote", doc.RootElement.GetProperty("environment").GetString());
                Assert.True(doc.RootElement.GetProperty("store").GetBoolean());
                Assert.False(doc.RootElement.TryGetProperty("background", out _));
                Assert.False(doc.RootElement.TryGetProperty("stream", out _));
                Assert.False(doc.RootElement.TryGetProperty("generation_config", out _));
                Assert.False(doc.RootElement.TryGetProperty("agent_config", out _));
            },
            get =>
            {
                Assert.Equal(HttpMethod.Get, get.Method);
                Assert.EndsWith("/v1beta/interactions/interaction-antigravity-1", get.RequestUri!.ToString());
            },
            delete =>
            {
                Assert.Equal(HttpMethod.Delete, delete.Method);
                Assert.EndsWith("/v1beta/interactions/interaction-antigravity-1", delete.RequestUri!.ToString());
            });
    }

    [Fact]
    public async Task StreamingAntigravityUsesNativeStreamAndDeletesStoredInteraction()
    {
        var handler = new RecordingHandler([
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = SseContent(
                    new
                    {
                        event_type = "interaction.created",
                        event_id = "event-1",
                        interaction = new
                        {
                            id = "interaction-antigravity-stream-1",
                            agent = "antigravity-preview-05-2026",
                            status = "in_progress"
                        }
                    },
                    new
                    {
                        event_type = "step.delta",
                        event_id = "event-2",
                        index = 0,
                        delta = new
                        {
                            type = "text",
                            text = "sandbox result"
                        }
                    },
                    new
                    {
                        event_type = "interaction.completed",
                        event_id = "event-3",
                        interaction = new
                        {
                            id = "interaction-antigravity-stream-1",
                            agent = "antigravity-preview-05-2026",
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
            Model = "google/antigravity-preview-05-2026",
            Input = new InteractionsInput("work in the sandbox"),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["environment"] = JsonSerializer.SerializeToElement("env_abc123", JsonSerializerOptions.Web)
            }
        }))
        {
            parts.Add(part);
        }

        Assert.Contains(parts, part => part is InteractionCreatedEvent { Interaction.Agent: "antigravity-preview-05-2026" });
        Assert.Contains(parts, part => part is InteractionStepDeltaEvent { Delta.Type: "text", Delta.Text: "sandbox result" });
        Assert.Contains(parts, part => part is InteractionCompletedEvent { Interaction.Status: "completed" });

        Assert.Collection(handler.Requests,
            create =>
            {
                Assert.Equal(HttpMethod.Post, create.Method);
                using var doc = JsonDocument.Parse(create.Body!);
                Assert.Equal("antigravity-preview-05-2026", doc.RootElement.GetProperty("agent").GetString());
                Assert.Equal("env_abc123", doc.RootElement.GetProperty("environment").GetString());
                Assert.True(doc.RootElement.GetProperty("stream").GetBoolean());
                Assert.True(doc.RootElement.GetProperty("store").GetBoolean());
                Assert.False(doc.RootElement.TryGetProperty("background", out _));
                Assert.False(doc.RootElement.TryGetProperty("agent_config", out _));
            },
            delete => Assert.Equal(HttpMethod.Delete, delete.Method));

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

    private static bool InvokeShouldSkipArchivePath(string path)
    {
        var method = typeof(GoogleAIProvider).GetMethod(
            "ShouldSkipGoogleAgentArchiveEntryPath",
            BindingFlags.NonPublic | BindingFlags.Static)!;

        return (bool)method.Invoke(null, [path])!;
    }

    private static AIStreamEvent InvokeMarkGoogleAgentUnifiedToolEventProviderExecuted(GoogleAIProvider provider, AIStreamEvent streamEvent)
    {
        var method = typeof(GoogleAIProvider).GetMethod(
            "MarkGoogleAgentUnifiedToolEventProviderExecuted",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

        return (AIStreamEvent)method.Invoke(provider, [streamEvent])!;
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

    private static string CreateTempCaptureRoot()
        => Path.Combine(Path.GetTempPath(), "aihappey-google-agent-capture-tests", Guid.NewGuid().ToString("N"));

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
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
