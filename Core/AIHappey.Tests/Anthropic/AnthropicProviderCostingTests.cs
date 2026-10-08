using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Anthropic;

public class AnthropicProviderCostingTests
{
    private const string MessageModel = "claude-haiku-4-5-20251001";
    private const decimal TokenCost = 0.000127m;

    public static IEnumerable<object?[]> ManagedAgentCostCases()
    {
        foreach (var streaming in new[] { false, true })
        {
            // Newly created sessions have no historical spend, even if create omits pricing.
            yield return [streaming, false, null, ListCost("\"2\""), null, 0.02m];
            yield return [streaming, false, null, ListCost("2.75"), null, 0.0275m];
            yield return [streaming, false, null, ListCost("\"2.5e-1\""), null, 0.0025m];
            yield return [streaming, false, null, ListCost("0"), null, 0m];
            // Reused sessions bill only the increase, not their total or their search counts.
            yield return [streaming, true, ListCost("\"2\""), ListCost("\"5\""), null, 0.03m];
            yield return [streaming, true, ListCost("2.125"), ListCost("2.875"), null, 0.0075m];
            yield return [streaming, true, ListCost("2"), ListCost("2"), null, 0m];
            yield return [streaming, true, ListCost("5"), ListCost("2"), null, 0m];
            // Retrieved pricing wins over an earlier session.usage snapshot, including zero.
            yield return [streaming, true, ListCost("2"), ListCost("5"), ListCost("4"), 0.03m];
            yield return [streaming, false, null, ListCost("0"), ListCost("4"), 0m];
            // A usage event can supply the price if the final session omits it.
            yield return [streaming, true, ListCost("2"), null, ListCost("5"), 0.03m];
            yield return [streaming, true, ListCost("2"), "{}", ListCost("5"), 0.03m];
            yield return [streaming, true, null, ListCost("5"), null, null];
            yield return [streaming, false, null, null, null, null];
            foreach (var invalid in new[]
                     {
                         ListCost("\"invalid\""), ListCost("-2"), ListCost("null"), ListCost("true"),
                         ListCost("2", "EUR"), "{}", ListCost("\"1,25\""), ListCost("\"1e100\"")
                     })
            {
                yield return [streaming, false, null, invalid, null, null];
                yield return [streaming, true, invalid, ListCost("5"), null, null];
            }
        }
    }

    [Theory]
    [MemberData(nameof(ManagedAgentCostCases))]
    public async Task Managed_agents_emit_only_current_request_cost_and_preserve_usage(
        bool streaming, bool existing, string? baseline, string? final, string? streamed, decimal? expected)
    {
        var fixture = new ManagedAgentFixture(existing, baseline, final, streamed);
        var provider = CreateProvider(fixture.Respond);
        var request = ManagedAgentRequest(existing);

        if (streaming)
        {
            var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(request));
            var finish = Assert.Single(events, evt => evt.Event.Type == "finish");
            var data = Assert.IsType<AIFinishEventData>(finish.Event.Data);
            Assert.Equal(expected, data.MessageMetadata?.Gateway?.Cost);
            Assert.Equal(expected, GatewayCost(finish.Metadata));
            var uiFinish = Assert.IsType<FinishUIPart>(Assert.Single(finish.Event.ToUIMessagePart("anthropic")));
            Assert.Equal(expected, uiFinish.MessageMetadata?.Gateway?.Cost);
            Assert.Equal(7, data.MessageMetadata!.Usage.GetProperty("server_tool_use")
                .GetProperty("web_search_requests").GetInt32());
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal(expected, GatewayCost(response.Metadata));
            Assert.Equal(expected, GatewayCost(response.ToResponseResult().Metadata));
            var usage = JsonSerializer.SerializeToElement(response.Usage);
            Assert.Equal(7, usage.GetProperty("server_tool_use").GetProperty("web_search_requests").GetInt32());
        }

        Assert.Equal(1, fixture.Submissions);
        Assert.Equal(existing ? 2 : 1, fixture.SessionReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_agent_recovery_does_not_rebill_historical_usage(bool streaming)
    {
        var fixture = new ManagedAgentFixture(true, ListCost("2"), ListCost("5"), ListCost("4"), recovery: true);
        var provider = CreateProvider(fixture.Respond);
        var request = new AIRequest
        {
            ProviderId = "anthropic", Model = "anthropic/agent/agent_123/env_123",
            Metadata = new Dictionary<string, object?> { ["anthropic"] = new { sessionId = "sess_cost" } },
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
                                Type = "tool-output-available", ToolCallId = "evt_custom", ToolName = "lookup",
                                State = "output-available", ProviderExecuted = false, Output = "Already submitted"
                            }
                        ]
                    }
                ]
            }
        };

        if (streaming)
        {
            var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(request));
            var finish = Assert.Single(events, evt => evt.Event.Type == "finish");
            Assert.Equal(0m, Assert.IsType<AIFinishEventData>(finish.Event.Data).MessageMetadata?.Gateway?.Cost);
            Assert.Equal(0m, GatewayCost(finish.Metadata));
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal(0m, GatewayCost(response.Metadata));
        }

        Assert.Equal(0, fixture.Submissions);
        Assert.Equal(1, fixture.SessionReads);
        Assert.Equal(0, fixture.LiveStreams);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_agent_chat_completions_forward_the_request_cost(bool streaming)
    {
        var fixture = new ManagedAgentFixture(false, null, ListCost("5"), ListCost("4"));
        var provider = CreateProvider(fixture.Respond);
        var options = ManagedAgentRequest(false).ToChatCompletionOptions("anthropic");
        if (streaming)
        {
            var updates = await FixtureAssertions.CollectAsync(provider.CompleteChatStreamingAsync(options, default));
            Assert.Equal(0.05m, GatewayCost(updates.Last().AdditionalProperties?.GetValueOrDefault("metadata")));
        }
        else
        {
            var response = await provider.CompleteChatAsync(options);
            Assert.Equal(0.05m, GatewayCost(response.AdditionalProperties?.GetValueOrDefault("metadata")));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Managed_agents_reuse_the_pre_submission_tool_reconciliation_snapshot(bool streaming)
    {
        var fixture = new ManagedAgentFixture(true, ListCost("2"), ListCost("5"), null);
        var provider = CreateProvider(fixture.Respond);
        var request = ManagedAgentRequest(true, reconcileTools: true);
        if (streaming)
        {
            var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(request));
            Assert.Equal(0.03m, GatewayCost(Assert.Single(events, evt => evt.Event.Type == "finish").Metadata));
        }
        else
            Assert.Equal(0.03m, GatewayCost((await provider.ExecuteUnifiedAsync(request)).Metadata));

        // One reconciliation read before submission plus one final read: no extra baseline fetch.
        Assert.Equal(2, fixture.SessionReads);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Messages_add_reported_search_fees_to_existing_token_cost(int? searches)
    {
        var usage = new MessagesUsage
        {
            InputTokens = 12,
            OutputTokens = 23,
            ServerToolUse = searches.HasValue
                ? new MessagesServerToolUsage { WebSearchRequests = searches, WebFetchRequests = 10 }
                : null
        };
        var provider = CreateProvider(_ => JsonResponse(new MessagesResponse
        {
            Id = "msg_cost", Type = "message", Role = "assistant", Model = MessageModel,
            Content = [], StopReason = "end_turn", Usage = usage
        }));
        var response = await provider.MessagesAsync(MessagesRequest(), []);
        var expected = TokenCost + (searches ?? 0) * 0.01m;

        Assert.Equal(expected, GatewayCost(response.Metadata));
        Assert.Equal(searches, response.Usage!.ServerToolUse?.WebSearchRequests);
        var unified = response.ToUnifiedResponse("anthropic");
        Assert.Equal(expected, GatewayCost(unified.Metadata));
        Assert.Equal(expected, GatewayCost(unified.ToResponseResult().Metadata));
        var chat = await provider.CompleteChatAsync(MessagesRequest().ToUnifiedRequest("anthropic")
            .ToChatCompletionOptions("anthropic"));
        Assert.Equal(expected, GatewayCost(chat.AdditionalProperties?.GetValueOrDefault("metadata")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(2)]
    public async Task Messages_stream_merges_cumulative_and_partial_tool_usage_without_double_charging(int? searches)
    {
        var provider = CreateProvider(_ => SseResponse(MessagesStreamFixture(searches)));
        var expected = TokenCost + (searches ?? 0) * 0.01m;
        var parts = await FixtureAssertions.CollectAsync(provider.MessagesStreamingAsync(MessagesRequest(), []));
        var stop = Assert.Single(parts, part => part.Type == "message_stop");
        Assert.Equal(expected, GatewayCost(stop.Metadata));

        var events = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(
            MessagesRequest().ToUnifiedRequest("anthropic")));
        var finish = Assert.Single(events, evt => evt.Event.Type == "finish");
        var data = Assert.IsType<AIFinishEventData>(finish.Event.Data);
        Assert.Equal(expected, data.MessageMetadata?.Gateway?.Cost);
        var uiFinish = Assert.IsType<FinishUIPart>(Assert.Single(finish.Event.ToUIMessagePart("anthropic")));
        Assert.Equal(expected, uiFinish.MessageMetadata?.Gateway?.Cost);

        var updates = await FixtureAssertions.CollectAsync(provider.CompleteChatStreamingAsync(
            MessagesRequest().ToUnifiedRequest("anthropic").ToChatCompletionOptions("anthropic"), default));
        Assert.Equal(expected, GatewayCost(updates.Last().AdditionalProperties?.GetValueOrDefault("metadata")));

        var responses = await FixtureAssertions.CollectAsync(provider.StreamUnifiedAsync(
            MessagesRequest().ToUnifiedRequest("anthropic")).ToResponseStreamParts());
        var completed = Assert.Single(responses.OfType<AIHappey.Responses.Streaming.ResponseCompleted>());
        Assert.Equal(expected, GatewayCost(completed.Response.Metadata));
    }

    [Fact]
    public async Task Messages_search_only_usage_still_emits_the_search_cost()
    {
        var provider = CreateProvider(_ => JsonResponse(new MessagesResponse
        {
            Id = "msg_search", Type = "message", Role = "assistant", Model = MessageModel,
            Content = [], Usage = new MessagesUsage
            {
                InputTokens = 0, OutputTokens = 0,
                ServerToolUse = new MessagesServerToolUsage { WebSearchRequests = 2 }
            }
        }));
        var response = await provider.MessagesAsync(MessagesRequest(), []);
        Assert.Equal(0.02m, GatewayCost(response.Metadata));
    }

    private static string MessagesStreamFixture(int? searches)
    {
        var parts = new MessageStreamPart[]
        {
            new()
            {
                Type = "message_start",
                Message = new MessagesResponse
                {
                    Id = "msg_stream_cost", Type = "message", Role = "assistant", Model = MessageModel,
                    Usage = new MessagesUsage
                    {
                        InputTokens = 12, OutputTokens = 1,
                        ServerToolUse = searches.HasValue ? new MessagesServerToolUsage { WebSearchRequests = 1 } : null
                    }
                }
            },
            new()
            {
                Type = "message_delta",
                Usage = new MessagesUsage
                {
                    OutputTokens = 23,
                    ServerToolUse = new MessagesServerToolUsage { WebSearchRequests = searches }
                }
            },
            new()
            {
                Type = "message_delta",
                Usage = new MessagesUsage
                {
                    OutputTokens = 23,
                    ServerToolUse = new MessagesServerToolUsage { WebSearchRequests = searches }
                }
            },
            new()
            {
                Type = "message_delta", Delta = new MessageStreamDelta { StopReason = "end_turn" },
                Usage = new MessagesUsage
                {
                    OutputTokens = 23,
                    ServerToolUse = new MessagesServerToolUsage { WebFetchRequests = 8 }
                }
            },
            new() { Type = "message_stop" }
        };
        return string.Concat(parts.Select(part => $"data: {JsonSerializer.Serialize(part, MessagesJson.Default)}\n\n"));
    }

    private static MessagesRequest MessagesRequest() => new()
    {
        Model = MessageModel, MaxTokens = 128,
        Messages = [new MessageParam { Role = "user", Content = new MessagesContent("Hello") }]
    };

    private static AIRequest ManagedAgentRequest(bool existing, bool reconcileTools = false) => new()
    {
        ProviderId = "anthropic", Model = "anthropic/agent/agent_123/env_123",
        Tools = reconcileTools ? [] : null,
        Metadata = existing ? new Dictionary<string, object?> { ["anthropic"] = new { sessionId = "sess_cost" } } : null,
        Input = new AIInput { Text = "Hello" }
    };

    private static string ListCost(string amountJson, string currency = "USD")
        => $"{{\"amount\":{amountJson},\"currency\":\"{currency}\"}}";

    private static Dictionary<string, object?> ManagedUsage(string? listCost)
    {
        var usage = new Dictionary<string, object?>
        {
            ["input_tokens"] = 3, ["output_tokens"] = 48,
            ["cache_creation"] = new { ephemeral_1h_input_tokens = 0, ephemeral_5m_input_tokens = 4987 },
            ["server_tool_use"] = new { web_search_requests = 7, web_fetch_requests = 10 }
        };
        if (listCost is not null)
            usage["list_cost"] = JsonSerializer.Deserialize<JsonElement>(listCost);
        return usage;
    }

    private static decimal? GatewayCost(object? metadata)
    {
        var json = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("gateway", out var gateway)
            && gateway.TryGetProperty("cost", out var cost) && cost.TryGetDecimal(out var value)
                ? value : null;
    }

    private sealed class ManagedAgentFixture(
        bool existing, string? baseline, string? final, string? streamed, bool recovery = false)
    {
        public int Submissions { get; private set; }
        public int SessionReads { get; private set; }
        public int LiveStreams { get; private set; }

        public HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/v1/sessions" && request.Method == HttpMethod.Post)
            {
                Assert.False(existing);
                return Session(baseline);
            }
            if (path == "/v1/sessions/sess_cost" && request.Method == HttpMethod.Get)
            {
                SessionReads++;
                return Session(Submissions == 0 && !recovery ? baseline : final);
            }
            if (path == "/v1/sessions/sess_cost/events" && request.Method == HttpMethod.Post)
            {
                Assert.False(recovery);
                // The baseline must be fetched before submitting new work on a reused session.
                Assert.Equal(existing ? 1 : 0, SessionReads);
                Submissions++;
                return JsonResponse(new { data = new[] { new { id = "evt_user", type = "user.message" } } });
            }
            if (path == "/v1/sessions/sess_cost/events/stream")
            {
                LiveStreams++;
                // Opening happens before submission; exercise real live SSE rather than polling.
                return SseResponse(string.Concat(TurnEvents().Select(evt =>
                    $"event: message\ndata: {JsonSerializer.Serialize(evt, JsonSerializerOptions.Web)}\n\n")));
            }
            if (path == "/v1/sessions/sess_cost/events" && request.Method == HttpMethod.Get)
            {
                var history = new List<object>();
                if (recovery)
                {
                    history.Add(new { id = "evt_custom", type = "agent.custom_tool_use", name = "lookup", input = new { } });
                    history.Add(new { id = "evt_result", type = "user.custom_tool_result", custom_tool_use_id = "evt_custom" });
                }
                else
                    history.Add(new { id = "evt_user", type = "user.message" });
                history.AddRange(TurnEvents());
                return JsonResponse(new { data = history, next_page = (string?)null });
            }
            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        }

        private HttpResponseMessage Session(string? listCost) => JsonResponse(new
        {
            id = "sess_cost", status = "idle", environment_id = "env_123",
            agent = new { id = "agent_123", version = 1 }, usage = ManagedUsage(listCost)
        });

        private IEnumerable<object> TurnEvents()
        {
            yield return new
            {
                id = "evt_msg", type = "agent.message", processed_at = "2026-10-08T10:00:00Z",
                content = new[] { new { type = "text", text = "Done" } }
            };
            if (streamed is not null)
                yield return new { id = "evt_usage", type = "session.usage", usage = ManagedUsage(streamed) };
            yield return new
            {
                id = "evt_idle", type = "session.status_idle", processed_at = "2026-10-08T10:00:01Z",
                stop_reason = new { type = "end_turn" }
            };
        }
    }

    private static AnthropicProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => new(new ApiKeyResolver(), new HttpClientFactory(new HttpClient(new Handler(respond))));

    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage SseResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
    };

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = respond(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
