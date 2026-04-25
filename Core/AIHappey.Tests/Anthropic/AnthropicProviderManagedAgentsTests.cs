using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Anthropic;

public class AnthropicProviderManagedAgentsTests
{
    [Fact]
    public async Task ExecuteUnifiedAsync_creates_managed_agent_session_and_returns_session_tool_call()
    {
        var createSessionCalls = 0;

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions")
            {
                createSessionCalls++;
                Assert.Equal("managed-agents-2026-04-01", TryGetSingleHeaderValue(request, "anthropic-beta"));

                return JsonResponse(new
                {
                    id = "sess_123",
                    status = "idle",
                    environment_id = "env_123",
                    usage = new { input_tokens = 0, output_tokens = 0 },
                    agent = new { id = "agent_123", version = 1 }
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_123/events")
            {
                return JsonResponse(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "evt_user_1",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Hello managed agent" } }
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_123/events")
            {
                return JsonResponse(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = "evt_user_1",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Hello managed agent" } }
                        },
                        new
                        {
                            id = "evt_msg_1",
                            type = "agent.message",
                            processed_at = "2026-04-25T11:00:00Z",
                            content = new[] { new { type = "text", text = "Hi from managed agent" } }
                        },
                        new
                        {
                            id = "evt_idle_1",
                            type = "session.status_idle",
                            processed_at = "2026-04-25T11:00:01Z",
                            stop_reason = new { type = "end_turn" }
                        }
                    },
                    next_page = (string?)null
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_123")
            {
                return JsonResponse(new
                {
                    id = "sess_123",
                    status = "idle",
                    environment_id = "env_123",
                    usage = new { input_tokens = 11, output_tokens = 7, total_tokens = 18 },
                    agent = new { id = "agent_123", version = 1 }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var response = await provider.ExecuteUnifiedAsync(CreateManagedAgentRequest());

        Assert.Equal(1, createSessionCalls);
        Assert.Equal("completed", response.Status);

        var outputItems = response.Output?.Items;
        Assert.NotNull(outputItems);
        Assert.Equal(2, outputItems!.Count);

        var sessionToolPart = Assert.IsType<AIToolCallContentPart>(Assert.Single(outputItems[0].Content!));
        Assert.True(sessionToolPart.ProviderExecuted);
        Assert.Equal("create_managed_agent_session", sessionToolPart.ToolName);

        var sessionToolOutput = JsonSerializer.SerializeToElement(sessionToolPart.Output, JsonSerializerOptions.Web);
        var structuredContent = sessionToolOutput.GetProperty("structuredContent");
        Assert.Equal("sess_123", structuredContent.GetProperty("sessionId").GetString());
        Assert.Equal("agent_123", structuredContent.GetProperty("agentId").GetString());
        Assert.Equal("env_123", structuredContent.GetProperty("environmentId").GetString());

        var textPart = Assert.IsType<AITextContentPart>(Assert.Single(outputItems[1].Content!));
        Assert.Equal("Hi from managed agent", textPart.Text);
    }

    [Fact]
    public async Task ExecuteUnifiedAsync_reuses_session_from_prior_provider_executed_tool_output()
    {
        var createSessionCalls = 0;

        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions")
            {
                createSessionCalls++;
                return JsonResponse(new { id = "should_not_be_called" });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_existing/events")
            {
                return JsonResponse(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "evt_user_2",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Follow-up" } }
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_existing/events")
            {
                return JsonResponse(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = "evt_user_2",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Follow-up" } }
                        },
                        new
                        {
                            id = "evt_msg_2",
                            type = "agent.message",
                            processed_at = "2026-04-25T11:00:02Z",
                            content = new[] { new { type = "text", text = "Reused existing session" } }
                        },
                        new
                        {
                            id = "evt_idle_2",
                            type = "session.status_idle",
                            processed_at = "2026-04-25T11:00:03Z",
                            stop_reason = new { type = "end_turn" }
                        }
                    },
                    next_page = (string?)null
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_existing")
            {
                return JsonResponse(new
                {
                    id = "sess_existing",
                    status = "idle",
                    usage = new { input_tokens = 4, output_tokens = 5, total_tokens = 9 }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var response = await provider.ExecuteUnifiedAsync(CreateManagedAgentRequestWithExistingSession());

        Assert.Equal(0, createSessionCalls);

        var outputItems = response.Output?.Items;
        Assert.NotNull(outputItems);
        Assert.Single(outputItems!);
        var textPart = Assert.IsType<AITextContentPart>(Assert.Single(outputItems[0].Content!));
        Assert.Equal("Reused existing session", textPart.Text);
    }

    [Fact]
    public async Task StreamUnifiedAsync_emits_managed_agent_session_tool_and_text_events()
    {
        var handler = new StaticResponseHttpMessageHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions")
            {
                return JsonResponse(new
                {
                    id = "sess_stream",
                    status = "idle",
                    environment_id = "env_123",
                    usage = new { input_tokens = 0, output_tokens = 0 },
                    agent = new { id = "agent_123", version = 1 }
                });
            }

            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_stream/events")
            {
                return JsonResponse(new
                {
                    data = new[]
                    {
                        new
                        {
                            id = "evt_user_stream",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Hello managed agent" } }
                        }
                    }
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_stream/events")
            {
                return JsonResponse(new
                {
                    data = new object[]
                    {
                        new
                        {
                            id = "evt_user_stream",
                            type = "user.message",
                            content = new[] { new { type = "text", text = "Hello managed agent" } }
                        },
                        new
                        {
                            id = "evt_msg_stream",
                            type = "agent.message",
                            processed_at = "2026-04-25T11:00:04Z",
                            content = new[] { new { type = "text", text = "Streaming from agent" } }
                        },
                        new
                        {
                            id = "evt_idle_stream",
                            type = "session.status_idle",
                            processed_at = "2026-04-25T11:00:05Z",
                            stop_reason = new { type = "end_turn" }
                        }
                    },
                    next_page = (string?)null
                });
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/v1/sessions/sess_stream")
            {
                return JsonResponse(new
                {
                    id = "sess_stream",
                    status = "idle",
                    usage = new { input_tokens = 5, output_tokens = 6, total_tokens = 11 }
                });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent($"Unhandled request: {request.Method} {request.RequestUri}")
            };
        });

        var provider = CreateProvider(handler);
        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(CreateManagedAgentRequest()));
        var eventTypes = events.Select(streamEvent => streamEvent.Event.Type).ToList();

        FixtureAssertions.AssertContainsSubsequence(
            eventTypes,
            "tool-input-available",
            "tool-output-available",
            "text-start",
            "text-delta",
            "text-end",
            "finish");
    }

    private static AnthropicProvider CreateProvider(HttpMessageHandler handler)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("https://api.anthropic.com/")
            }));

    private static AIRequest CreateManagedAgentRequest()
        => new()
        {
            ProviderId = "anthropic",
            Model = "anthropic/agent/agent_123/env_123",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "Hello managed agent" }
                        ]
                    }
                ]
            }
        };

    private static AIRequest CreateManagedAgentRequestWithExistingSession()
        => new()
        {
            ProviderId = "anthropic",
            Model = "anthropic/agent/agent_123/env_123",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "assistant",
                        Content =
                        [
                            new AIToolCallContentPart
                            {
                                ToolCallId = "anthropic-create-session-sess_existing",
                                ToolName = "create_managed_agent_session",
                                //Type = "text",
                                 Type = "tool-output-available",
                                ProviderExecuted = true,
                                State = "output-available",
                                Output = new CallToolResult
                                {
                                    Content = [],
                                    StructuredContent = JsonSerializer.SerializeToElement(new
                                    {
                                        sessionId = "sess_existing",
                                        agentId = "agent_123",
                                        environmentId = "env_123"
                                    }, JsonSerializerOptions.Web)
                                },
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["anthropic"] = JsonSerializer.SerializeToElement(new
                                    {
                                        sessionId = "sess_existing",
                                        agentId = "agent_123",
                                        environmentId = "env_123"
                                    }, JsonSerializerOptions.Web)
                                }
                            }
                        ]
                    },
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AITextContentPart { Type = "text", Text = "Follow-up" }
                        ]
                    }
                ]
            }
        };

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload, JsonSerializerOptions.Web),
                Encoding.UTF8,
                "application/json")
        };

    private static string? TryGetSingleHeaderValue(HttpRequestMessage request, string headerName)
        => request.Headers.TryGetValues(headerName, out var values)
            ? values.SingleOrDefault()
            : null;

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
