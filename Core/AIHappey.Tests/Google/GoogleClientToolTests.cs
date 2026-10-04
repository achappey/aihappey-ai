using System.Net;
using System.Reflection;
using System.Text;
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

public sealed class GoogleClientToolTests
{
    [Theory]
    [InlineData("google/antigravity-preview-latest")]
    [InlineData("google/agents/customer-sentinel")]
    [InlineData("google/gemini-3.5-flash")]
    public async Task StreamingClassifiesOnlyRequestFunctionsAsClientExecuted(string model)
    {
        var handler = new RecordingHandler(SseResponse(
            """{"event_type":"interaction.created","interaction":{"id":"i-1","status":"in_progress"}}""",
            """{"event_type":"step.start","index":0,"step":{"type":"function_call","id":"client-1","name":"country_widget","arguments":{}}}""",
            """{"event_type":"step.delta","index":0,"delta":{"type":"arguments_delta","arguments":"{\"countryCode\":\"PL\"}"}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"step.start","index":1,"step":{"type":"function_result","call_id":"client-1"}}""",
            """{"event_type":"step.delta","index":1,"delta":{"type":"function_result","result":{"ok":true},"signature":"client-signature"}}""",
            """{"event_type":"step.stop","index":1}""",
            """{"event_type":"step.start","index":2,"step":{"type":"function_call","id":"native-1","name":"write_file","arguments":{"path":"result.txt"}}}""",
            """{"event_type":"step.stop","index":2}""",
            """{"event_type":"step.start","index":3,"step":{"type":"function_result","call_id":"native-1","name":"write_file","result":{"ok":true}}}""",
            """{"event_type":"step.stop","index":3}""",
            """{"event_type":"interaction.completed","interaction":{"id":"i-1","status":"completed"}}"""));
        var events = await Stream(CreateProvider(handler), Request(model));
        var clientStart = Assert.IsType<AIToolInputStartEventData>(events.Single(e => e.Event.Type == "tool-input-start" && e.Event.Id == "client-1").Event.Data);
        var clientInputEvent = events.Single(e => e.Event.Type == "tool-input-available" && e.Event.Id == "client-1");
        var clientInput = Assert.IsType<AIToolInputAvailableEventData>(clientInputEvent.Event.Data);
        Assert.False(clientStart.ProviderExecuted);
        Assert.False(clientInput.ProviderExecuted);
        Assert.Equal("PL", JsonSerializer.SerializeToElement(clientInput.Input).GetProperty("countryCode").GetString());
        var clientOutput = Assert.IsType<AIToolOutputAvailableEventData>(events.Single(e => e.Event.Type == "tool-output-available" && e.Event.Id == "client-1").Event.Data);
        Assert.False(clientOutput.ProviderExecuted);
        Assert.Equal("client-signature", clientOutput.ProviderMetadata!["google"]["signature"]);
        Assert.All(events.Where(e => e.Event.Id == "native-1"), e =>
        {
            if (e.Event.Data is AIToolInputStartEventData start) Assert.True(start.ProviderExecuted);
            if (e.Event.Data is AIToolInputAvailableEventData input) Assert.True(input.ProviderExecuted);
            if (e.Event.Data is AIToolOutputAvailableEventData output) Assert.True(output.ProviderExecuted);
        });
        var uiInput = Assert.Single(clientInputEvent.Event.ToUIMessagePart("google").OfType<ToolCallPart>());
        Assert.False(uiInput.ProviderExecuted);
        Assert.Contains(clientInputEvent.Event.ToUIMessagePart("google"), p => p is ToolApprovalRequestUIPart);
        using var payload = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Contains(payload.RootElement.GetProperty("tools").EnumerateArray(), t => t.GetProperty("name").GetString() == "country_widget");
    }

    [Theory]
    [InlineData("{\"cca\":\"PL\"}", new[] { "{\"cca\":\"PL\"}" }, "{\"cca\":\"PL\"}")]
    [InlineData("{\"cca\":\"PL\"}", new[] { "{\"cca\":", "\"PL\"}" }, "{\"cca\":\"PL\"}")]
    [InlineData("{}", new[] { "{\"text\":\"", "ha", "ha", "\"}" }, "{\"text\":\"haha\"}")]
    [InlineData("{\"cca\":\"PL\"}", new string[] { }, "{\"cca\":\"PL\"}")]
    public async Task ClientStreamUsesDeltaArgumentsOrInitialFallbackWithoutConcatenatingBoth(string initial, string[] chunks, string expected)
    {
        // First case is the captured runtime call_2442063: complete start object
        // followed by the same JSON in arguments_delta, then step.stop.
        var raw = new List<string>
        {
            """{"event_type":"interaction.created","interaction":{"id":"i-1","status":"in_progress"}}""",
            $"{{\"event_type\":\"step.start\",\"index\":1,\"step\":{{\"type\":\"function_call\",\"id\":\"call_2442063\",\"name\":\"github_rest_countries_get_detail\",\"arguments\":{initial},\"signature\":\"sig-1\"}}}}"
        };
        raw.AddRange(chunks.Select(arguments => JsonSerializer.Serialize(new
        {
            event_type = "step.delta", index = 1, delta = new { type = "arguments_delta", arguments }
        })));
        raw.Add("""{"event_type":"step.stop","index":1}""");
        raw.Add("""{"event_type":"interaction.completed","interaction":{"id":"i-1","status":"completed"}}""");
        var events = await Stream(CreateProvider(new RecordingHandler(SseResponse(raw.ToArray()))),
            Request(tools: [new AIToolDefinition { Name = "github_rest_countries_get_detail" }]));
        var inputEvent = Assert.Single(events, e => e.Event.Type == "tool-input-available");
        var input = Assert.IsType<AIToolInputAvailableEventData>(inputEvent.Event.Data);
        Assert.Equal("call_2442063", inputEvent.Event.Id);
        Assert.False(input.ProviderExecuted);
        Assert.Equal(JsonValueKind.Object, JsonSerializer.SerializeToElement(input.Input).ValueKind);
        Assert.Equal(expected, JsonSerializer.Serialize(input.Input));
        var ui = Assert.Single(inputEvent.Event.ToUIMessagePart("google").OfType<ToolCallPart>());
        var serializedUi = JsonSerializer.SerializeToElement(ui, JsonSerializerOptions.Web);
        Assert.Equal(JsonValueKind.Object, serializedUi.GetProperty("input").ValueKind);
        Assert.False(serializedUi.GetProperty("providerExecuted").GetBoolean());
    }

    [Theory]
    [InlineData("{}", new[] { "{\"text\":\"", "ha", "ha", "\"}" }, "{\"text\":\"haha\"}")]
    [InlineData("{\"countryCode\":\"PL\"}", new string[] { }, "{\"countryCode\":\"PL\"}")]
    [InlineData("{}", new string[] { }, "{}")]
    public void ArgumentAssemblyPreservesChunksAndInitialFallback(string initial, string[] chunks, string expected)
    {
        var providerId = $"arguments-test-{Guid.NewGuid():N}";
        var parts = new List<InteractionStreamEventPart>
        {
            new InteractionCreatedEvent(),
            new InteractionStepStartEvent
            {
                Index = 0, Step = new InteractionFunctionCallContent
                {
                    Id = "client-1", Name = "country_widget", Arguments = JsonSerializer.Deserialize<JsonElement>(initial)
                }
            }
        };
        parts.AddRange(chunks.Select(chunk => new InteractionStepDeltaEvent
        {
            Index = 0, Delta = new InteractionContentDeltaData
            {
                Type = "arguments_delta", AdditionalProperties = new() { ["arguments"] = JsonSerializer.SerializeToElement(chunk) }
            }
        }));
        parts.Add(new InteractionStepStopEvent { Index = 0 });
        var events = parts.SelectMany(part => part.ToUnifiedStreamEvent(providerId)).ToList();
        var input = Assert.IsType<AIToolInputAvailableEventData>(events.Single(e => e.Event.Type == "tool-input-available").Event.Data);
        Assert.Equal(expected, JsonSerializer.Serialize(input.Input, JsonSerializerOptions.Web));
        Assert.Equal(string.Concat(chunks), string.Concat(events.Select(e => e.Event.Data).OfType<AIToolInputDeltaEventData>().Select(d => d.InputTextDelta)));
    }

    [Theory]
    [InlineData("provider-option")]
    [InlineData("native-definition")]
    [InlineData("raw-native-definition")]
    [InlineData("case-mismatch")]
    [InlineData("missing-tools")]
    [InlineData("reserved-state")]
    public async Task ToolProvenanceCannotAccidentallyCreateClientOwnership(string scenario)
    {
        var name = scenario == "reserved-state" ? "google_antigravity_state" : "country_widget";
        var tools = new List<AIToolDefinition>();
        Dictionary<string, object?>? metadata = null;
        if (scenario == "provider-option")
            metadata = new() { ["google"] = JsonSerializer.SerializeToElement(new { tools = new[] { new { type = "function", name } } }) };
        if (scenario == "native-definition")
            tools.Add(new AIToolDefinition { Name = name, Metadata = new() { ["interactions.tool.type"] = "mcp_server" } });
        if (scenario == "raw-native-definition")
            tools.Add(new AIToolDefinition { Name = name, Metadata = new() { ["interactions.tool.raw"] = JsonSerializer.SerializeToElement(new { type = "mcp_server", name }) } });
        if (scenario == "case-mismatch") tools.Add(new AIToolDefinition { Name = "Country_Widget" });
        if (scenario == "reserved-state") tools.Add(new AIToolDefinition { Name = name });
        var events = await Stream(CreateProvider(new RecordingHandler(FunctionStream(name))), Request(tools: tools, metadata: metadata));
        Assert.True(Assert.IsType<AIToolInputStartEventData>(events.Single(e => e.Event.Type == "tool-input-start").Event.Data).ProviderExecuted);
        Assert.True(Assert.IsType<AIToolInputAvailableEventData>(events.Single(e => e.Event.Type == "tool-input-available").Event.Data).ProviderExecuted);
    }

    [Fact]
    public async Task NativeEventWinsOverClientToolNameCollision()
    {
        var events = await Stream(CreateProvider(new RecordingHandler(SseResponse(
            """{"event_type":"interaction.created","interaction":{"id":"i-1","status":"in_progress"}}""",
            """{"event_type":"step.start","index":0,"step":{"type":"code_execution_call","id":"native-1","arguments":{"code":"print(1)","language":"python"}}}""",
            """{"event_type":"step.stop","index":0}""",
            """{"event_type":"interaction.completed","interaction":{"id":"i-1","status":"completed"}}"""))),
            Request(tools: [new AIToolDefinition { Name = "code_execution" }]));
        Assert.True(Assert.IsType<AIToolInputAvailableEventData>(events.Single(e => e.Event.Type == "tool-input-available").Event.Data).ProviderExecuted);
    }

    [Fact]
    public async Task OwnershipDoesNotLeakBetweenRequestsUsingTheSameProviderAndCallId()
    {
        var provider = CreateProvider(new RecordingHandler(FunctionStream("country_widget"), FunctionStream("country_widget")));
        var first = await Stream(provider, Request());
        var second = await Stream(provider, Request(tools: []));
        Assert.False(Assert.IsType<AIToolInputAvailableEventData>(first.Single(e => e.Event.Type == "tool-input-available").Event.Data).ProviderExecuted);
        Assert.True(Assert.IsType<AIToolInputAvailableEventData>(second.Single(e => e.Event.Type == "tool-input-available").Event.Data).ProviderExecuted);
    }

    [Theory]
    [InlineData("google/antigravity-preview-latest")]
    [InlineData("google/agents/customer-sentinel")]
    public async Task NonStreamingOwnershipPreservesNativeCallsAndContinuationState(string model)
    {
        var interaction = new
        {
            id = "i-1", agent = model.Replace("google/", "").Replace("agents/", ""), status = "completed", environment_id = "env-1",
            steps = new object[]
            {
                new { type = "model_output", content = new object[] { new { type = "function_call", id = "client-1", name = "country_widget", arguments = new { countryCode = "PL" }, signature = "client-signature" } } },
                new { type = "function_result", call_id = "client-1", result = new { ok = true } },
                new { type = "function_call", id = "native-1", name = "write_file", arguments = new { path = "result.txt" } },
                new { type = "function_result", call_id = "native-1", name = "write_file", result = new { ok = true } },
                new { type = "code_execution_call", id = "code-1", arguments = new { code = "print(1)", language = "python" } }
            }
        };
        var response = await CreateProvider(new RecordingHandler(JsonResponse(interaction), JsonResponse(interaction)))
            .ExecuteUnifiedAsync(Request(model, [new AIToolDefinition { Name = "country_widget" }, new AIToolDefinition { Name = "code_execution" }]));
        var tools = response.Output!.Items!.SelectMany(item => item.Content ?? []).OfType<AIToolCallContentPart>().ToList();
        Assert.All(tools.Where(t => t.ToolCallId == "client-1"), tool => Assert.False(tool.ProviderExecuted));
        Assert.All(tools.Where(t => t.ToolCallId != "client-1"), tool => Assert.True(tool.ProviderExecuted));
        var client = tools.Single(t => t.Type == "function_call" && t.ToolCallId == "client-1");
        Assert.Equal("client-signature", ((Dictionary<string, object?>)client.Metadata!["google"]!)["signature"]);
        Assert.Contains(tools, tool => tool.ToolName is "google_antigravity_state" or "google_custom_agent_state" && tool.ProviderExecuted == true);
    }

    [Fact]
    public void UnnamedErrorsUseCallOwnershipAndPreserveEventMetadata()
    {
        var nested = typeof(GoogleAIProvider).GetNestedType("GoogleToolExecutionOwnership", BindingFlags.NonPublic)!;
        var ownership = Activator.CreateInstance(nested, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [Request()], null)!;
        var method = typeof(GoogleAIProvider).GetMethod("MarkGoogleAgentUnifiedToolEventProviderExecuted", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var provider = CreateProvider(new RecordingHandler());
        var start = new AIStreamEvent
        {
            ProviderId = "google", Event = new AIEventEnvelope
            {
                Type = "tool-input-start", Id = "client-1", Data = new AIToolInputStartEventData { ToolName = "country_widget", ProviderExecuted = false }
            }
        };
        method.Invoke(provider, [start, ownership, new InteractionStepStartEvent { Step = new InteractionFunctionCallContent { Id = "client-1", Name = "country_widget" } }]);
        var metadata = new Dictionary<string, object?> { ["custom"] = "preserved" };
        var error = new AIStreamEvent
        {
            ProviderId = "google", Metadata = metadata, Event = new AIEventEnvelope
            {
                Type = "tool-output-error", Id = "error-event", Timestamp = DateTimeOffset.UtcNow, Metadata = metadata,
                Data = new AIToolOutputErrorEventData { ToolCallId = "client-1", ErrorText = "failed", ProviderExecuted = true, Dynamic = true }
            }
        };
        var mapped = (AIStreamEvent)method.Invoke(provider, [error, ownership, null])!;
        var data = Assert.IsType<AIToolOutputErrorEventData>(mapped.Event.Data);
        Assert.False(data.ProviderExecuted);
        Assert.True(data.Dynamic);
        Assert.Equal("failed", data.ErrorText);
        Assert.Equal(error.Event.Id, mapped.Event.Id);
        Assert.Equal(error.Event.Timestamp, mapped.Event.Timestamp);
        Assert.Same(metadata, mapped.Metadata);
        Assert.Same(metadata, mapped.Event.Metadata);
    }

    private static HttpResponseMessage FunctionStream(string name) => SseResponse(
        """{"event_type":"interaction.created","interaction":{"id":"i-1","status":"in_progress"}}""",
        JsonSerializer.Serialize(new { event_type = "step.start", index = 0, step = new { type = "function_call", id = "client-1", name, arguments = new { countryCode = "PL" } } }),
        """{"event_type":"step.stop","index":0}""",
        """{"event_type":"interaction.completed","interaction":{"id":"i-1","status":"completed"}}""");

    internal static AIRequest Request(string model = "google/antigravity-preview-latest", List<AIToolDefinition>? tools = null, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "google", Model = model, Input = new AIInput { Text = "Show Poland" }, Metadata = metadata,
            Tools = tools ?? [new AIToolDefinition { Name = "country_widget", InputSchema = new { type = "object" } }]
        };

    internal static async Task<List<AIStreamEvent>> Stream(GoogleAIProvider provider, AIRequest request)
    {
        var events = new List<AIStreamEvent>();
        await foreach (var e in provider.StreamUnifiedAsync(request)) events.Add(e);
        return events;
    }

    internal static GoogleAIProvider CreateProvider(RecordingHandler handler) => new(
        new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
        NullLogger<GoogleAIProvider>.Instance, new ClientFactory(new HttpClient(handler)));

    internal static HttpResponseMessage SseResponse(params string[] events) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(string.Join("\n\n", events.Select(e => $"data: {e}").Append("data: [DONE]")), Encoding.UTF8, "text/event-stream")
    };

    internal static HttpResponseMessage JsonResponse(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
    };

    private sealed class KeyResolver : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    internal sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        internal List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken));
            return queue.Dequeue();
        }
    }
}
