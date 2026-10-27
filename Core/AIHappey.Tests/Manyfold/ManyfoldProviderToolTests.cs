using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Manyfold;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Manyfold;

public sealed class ManyfoldProviderToolTests
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;
    private const string Search = "\n\n[tool_call:ToolSearch]\n{\"max_results\":1,\"query\":\"select:WebSearch\"}"
        + "\n\n[tool_result:toolu_search]\n[{\"tool_name\":\"WebSearch\",\"type\":\"tool_reference\"}]"
        + "\n\n[tool_call:WebSearch]\n{\"query\":\"Oekraïne oorlog\"}"
        + "\n\n[tool_result:toolu_failed]\n\"Failed to authenticate. API Error: 403 insufficient balance\""
        + "\n\n[tool_call:WebSearch]\n{\"query\":\"war news latest\"}"
        + "\n\n[tool_result:toolu_news]\n\"Links: [{\\\"title\\\":\\\"News\\\",\\\"url\\\":\\\"https://example.com\\\"}]\\nSummary\""
        + "**Het laatste nieuws**\n[Source](https://example.com)";
    private const string Shell = "\n\n[tool_call:Bash]\n{\"command\":\"python3 -c \\\"import docx; print('ok')\\\"\"}"
        + "\n\n[tool_result:toolu_python]\n\"Traceback:\\nModuleNotFoundError: No module named 'docx'\""
        + "I'll build the docx by hand."
        + "\n\n[tool_call:Bash]\n{\"command\":\"python3 -I - <<'E'\\nimport zipfile\\nE\"}"
        + "\n\n[tool_result:toolu_doc]\n\"HTTP/1.0 200 OK\\nShell cwd was reset\""
        + "Het Word-document staat klaar: https://example.com/random.docx";

    [Theory]
    [InlineData(Search, 3)]
    [InlineData(Shell, 2)]
    public async Task Complete_responses_expose_executed_tools_preserve_prose_and_native_debug_metadata(string transcript, int count)
    {
        var native = Completion(transcript);
        using var fixture = new Fixture(_ => Response(native));
        var result = await fixture.Provider.ExecuteUnifiedAsync(Request());
        var parts = result.Output!.Items![0].Content!;
        Assert.Equal("manyfold_session", Assert.IsType<AIToolCallContentPart>(parts[0]).ToolName);
        var tools = parts.OfType<AIToolCallContentPart>().Where(t => t.ToolName != "manyfold_session").ToArray();
        Assert.Equal(count, tools.Length);
        Assert.All(tools, t => { Assert.True(t.ProviderExecuted); Assert.Equal("output-available", t.State); Assert.NotNull(t.Output); });
        Assert.Equal(count, tools.Select(t => t.ToolCallId).Distinct().Count());
        var text = string.Concat(parts.OfType<AITextContentPart>().Select(t => t.Text));
        Assert.DoesNotContain("[tool_", text);
        Assert.Contains(transcript == Shell ? "I'll build" : "**Het laatste nieuws**", text);
        Assert.Contains("https://example.com", text);
        Assert.Equal(transcript, ((JsonElement)result.Metadata!["chatcompletions.response.raw"]!)
            .GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());
        var mapped = JsonSerializer.SerializeToElement(result.ToChatCompletion(), Json);
        var message = mapped.GetProperty("choices")[0].GetProperty("message");
        Assert.Equal(text, message.GetProperty("content").GetString());
        Assert.Equal(count + 1, message.GetProperty("tool_calls").GetArrayLength());
        Assert.All(message.GetProperty("tool_calls").EnumerateArray(), t => Assert.True(t.GetProperty("provider_executed").GetBoolean()));
        Assert.Equal("stop", mapped.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.Equal(3, mapped.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Theory]
    [InlineData(Search, 3)]
    [InlineData(Shell, 2)]
    public async Task Streaming_records_are_paired_and_text_segments_surround_tools_in_order(string transcript, int count)
    {
        using var fixture = StreamFixture([transcript]);
        var events = await Collect(fixture.Provider);
        AssertToolEvents(events, count);
        var prose = Text(events);
        Assert.DoesNotContain("[tool_", prose);
        Assert.Contains(transcript == Shell ? "I'll build the docx by hand." : "**Het laatste nieuws**", prose);
        Assert.Contains("https://example.com", prose);
        var finish = Assert.Single(events.Select(e => e.Event.Data).OfType<AIFinishEventData>());
        Assert.Equal("stop", finish.FinishReason);
        Assert.Equal(3, finish.TotalTokens);
        if (transcript == Shell)
        {
            var kinds = events.Where(e => e.Event.Type is "text-start" or "text-end"
                || e.Event.Type == "tool-input-available" && ((AIToolInputAvailableEventData)e.Event.Data!).ToolName == "Bash"
                || e.Event.Type == "tool-output-available" && ((AIToolOutputAvailableEventData)e.Event.Data!).ToolName == "Bash")
                .Select(e => e.Event.Type).ToArray();
            Assert.Equal(new[] { "tool-input-available", "tool-output-available", "text-start", "text-end",
                "tool-input-available", "tool-output-available", "text-start", "text-end" }, kinds);
        }
    }

    [Fact]
    public async Task Every_single_split_and_character_chunks_preserve_headers_json_unicode_and_result_ids()
    {
        var transcript = "Before\n\n[tool_call:Bash]\n"
            + JsonSerializer.Serialize(new { command = "echo \"hé 🌍\"; [tool_result:not-a-record]\\" }, Json)
            + "\n\n[tool_result:toolu_real]\n"
            + JsonSerializer.Serialize("line\n[tool_call:Fake]\n{\"nested\":true} \"escaped\" \\ 🌍", Json)
            + "After";
        for (var split = 1; split < transcript.Length; split++)
        {
            using var fixture = StreamFixture([transcript[..split], transcript[split..]]);
            var events = await Collect(fixture.Provider);
            AssertToolEvents(events, 1);
            Assert.Equal("BeforeAfter", Text(events));
            var output = Assert.Single(events.Select(e => e.Event.Data).OfType<AIToolOutputAvailableEventData>(), t => t.ToolName == "Bash");
            Assert.Equal("toolu_real", output.ProviderMetadata!["manyfold"]["tool_call_id"]);
            Assert.Contains("[tool_call:Fake]", ((JsonElement)output.Output).GetString());
        }
        using var characters = StreamFixture(transcript.Select(c => c.ToString()));
        AssertToolEvents(await Collect(characters.Provider), 1);
    }

    [Theory]
    [InlineData("{\"ok\":true}")]
    [InlineData("[{\"tool_name\":\"WebSearch\"}]")]
    [InlineData("\"a string\\nwith \\\"quotes\\\"\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task Result_payloads_remain_typed_json_in_both_modes(string payload)
    {
        var transcript = "[tool_call:Test]\n{}\n\n[tool_result:toolu_test]\n" + payload;
        using var fixture = StreamFixture(transcript.Select(c => c.ToString()));
        var events = await Collect(fixture.Provider);
        AssertToolEvents(events, 1);
        var result = events.Select(e => e.Event.Data).OfType<AIToolOutputAvailableEventData>().Last();
        Assert.Equal(payload, ((JsonElement)result.Output).GetRawText());
        using var complete = new Fixture(_ => Response(Completion(transcript)));
        var tool = (await complete.Provider.ExecuteUnifiedAsync(Request())).Output!.Items![0].Content!
            .OfType<AIToolCallContentPart>().Last();
        Assert.Equal(payload, ((JsonElement)tool.Output!).GetRawText());
    }

    [Theory]
    [InlineData(" \n\t")]
    [InlineData("Plain text with [tool_call:quoted] {} and a link https://example.com\n")]
    [InlineData("[tool_call:")]
    [InlineData("[tool_result:orphan]\n\"result\"")]
    [InlineData("[tool_call:Bash]\n{bad}\nAnswer")]
    [InlineData("[tool_call:Bash]\n{\"command\":\"unfinished")]
    [InlineData("[tool_call:Bash]\n[]")]
    [InlineData("[tool_call:Bash\nnot a record")]
    public async Task Unsupported_malformed_and_incomplete_records_remain_text_without_throwing(string transcript)
    {
        using var fixture = StreamFixture(transcript.Select(c => c.ToString()));
        var events = await Collect(fixture.Provider);
        Assert.Equal(transcript, Text(events));
        AssertToolEvents(events, 0);
        using var complete = new Fixture(_ => Response(Completion(transcript)));
        var response = await complete.Provider.ExecuteUnifiedAsync(Request());
        Assert.Equal(transcript, string.Concat(response.Output!.Items![0].Content!.OfType<AITextContentPart>().Select(t => t.Text)));
    }

    [Fact]
    public async Task An_unfinished_result_stays_visible_and_does_not_invent_tool_success()
    {
        const string tail = "\n\n[tool_result:toolu_missing]\n\"unfinished";
        using var fixture = StreamFixture(["[tool_call:Bash]\n{}" + tail]);
        var events = await Collect(fixture.Provider);
        Assert.Contains("[tool_result:toolu_missing]", Text(events));
        Assert.DoesNotContain(events, e => e.Event.Data is AIToolOutputAvailableEventData { ToolName: "Bash" });
        using var complete = new Fixture(_ => Response(Completion("[tool_call:Bash]\n{}" + tail)));
        var response = await complete.Provider.ExecuteUnifiedAsync(Request());
        var tool = response.Output!.Items![0].Content!.OfType<AIToolCallContentPart>().Last();
        Assert.Equal("input-available", tool.State);
        Assert.Null(tool.Output);
        var mapped = JsonSerializer.SerializeToElement(response.ToChatCompletion(), Json);
        var call = mapped.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")
            .EnumerateArray().First(t => t.GetProperty("function").GetProperty("name").GetString() == "Bash");
        Assert.False(call.GetProperty("function").TryGetProperty("output", out _));
    }

    [Fact]
    public async Task Chat_bridge_renders_tools_as_provider_executed_and_wraps_their_output()
    {
        using var fixture = StreamFixture([Search]);
        var parts = new List<UIMessagePart>();
        await foreach (var part in fixture.Provider.StreamAsync(new ChatRequest
        {
            Model = "manyfold/agent", Messages = [new() { Id = "user", Role = Role.user,
                Parts = [new TextUIPart { Text = "search" }] }]
        })) parts.Add(part);
        var inputs = parts.OfType<ToolCallPart>().Where(t => t.ToolName != "manyfold_session").ToArray();
        Assert.Equal(3, inputs.Length);
        Assert.All(inputs, t => Assert.True(t.ProviderExecuted));
        var outputs = parts.OfType<ToolOutputAvailablePart>().ToArray();
        Assert.Equal(4, outputs.Length);
        Assert.All(outputs, t => Assert.True(t.ProviderExecuted));
        Assert.DoesNotContain(parts.OfType<TextDeltaUIMessageStreamPart>(), p => p.Delta.Contains("[tool_", StringComparison.Ordinal));
        Assert.Contains(outputs, t => JsonSerializer.Serialize(t.Output, Json).Contains("insufficient balance", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Chat_completions_stream_does_not_reintroduce_transcripts_from_metadata_frames()
    {
        using var fixture = StreamFixture([Search]);
        var updates = new List<JsonElement>();
        await foreach (var update in fixture.Provider.CompleteChatStreamingAsync(new ChatCompletionOptions
        {
            Model = "manyfold/agent", Messages = [new() { Role = "user", Content = JsonSerializer.SerializeToElement("search") }]
        })) updates.Add(JsonSerializer.SerializeToElement(update, Json));
        var choices = updates.SelectMany(u => u.GetProperty("choices").EnumerateArray()).ToArray();
        var text = string.Concat(choices.Where(c => c.GetProperty("delta").TryGetProperty("content", out _))
            .Select(c => c.GetProperty("delta").GetProperty("content").GetString()));
        Assert.DoesNotContain("[tool_", text);
        Assert.Contains("Het laatste nieuws", text);
        var calls = choices.Where(c => c.GetProperty("delta").TryGetProperty("tool_calls", out _))
            .SelectMany(c => c.GetProperty("delta").GetProperty("tool_calls").EnumerateArray()).ToArray();
        Assert.Equal(8, calls.Length); // session plus three executions, each with input and output
        Assert.All(calls, c => Assert.True(c.GetProperty("provider_executed").GetBoolean()));
        Assert.Equal(4, calls.Select(c => c.GetProperty("id").GetString()).Distinct().Count());
    }

    [Fact]
    public async Task Follow_up_replay_uses_existing_mapping_without_synthetic_executable_calls()
    {
        using var fixture = new Fixture(_ => Response(Completion(Search)));
        var result = await fixture.Provider.ExecuteUnifiedAsync(Request());
        await fixture.Provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "manyfold", Model = "manyfold/agent", Input = new AIInput
            {
                Items = [new() { Role = "assistant", Content = result.Output!.Items![0].Content },
                    new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "thanks" }] }]
            }
        });
        var sent = JsonSerializer.Deserialize<JsonElement>(fixture.Handler.Bodies.Last());
        Assert.Equal("s1", sent.GetProperty("metadata").GetProperty("session_id").GetString());
        Assert.DoesNotContain(sent.GetProperty("messages").EnumerateArray(), m => m.TryGetProperty("tool_calls", out _));
        Assert.DoesNotContain(sent.GetProperty("messages").EnumerateArray(), m => m.GetProperty("role").GetString() == "tool");
        Assert.Contains("Het laatste nieuws", sent.GetProperty("messages")[0].GetProperty("content").GetString());
    }

    private static void AssertToolEvents(List<AIStreamEvent> events, int count)
    {
        var inputs = events.Where(e => e.Event.Data is AIToolInputAvailableEventData { ToolName: not "manyfold_session" }).ToArray();
        var outputs = events.Where(e => e.Event.Data is AIToolOutputAvailableEventData { ToolName: not "manyfold_session" }).ToArray();
        Assert.Equal(count, inputs.Length);
        Assert.Equal(count, outputs.Length);
        foreach (var input in inputs)
        {
            Assert.True(((AIToolInputAvailableEventData)input.Event.Data!).ProviderExecuted);
            var output = Assert.Single(outputs, e => e.Event.Id == input.Event.Id);
            Assert.True(((AIToolOutputAvailableEventData)output.Event.Data!).ProviderExecuted);
            Assert.True(events.IndexOf(input) < events.IndexOf(output));
        }
        var starts = events.Where(e => e.Event.Type == "text-start").ToArray();
        Assert.Equal(starts.Length, events.Count(e => e.Event.Type == "text-end"));
        Assert.Equal(starts.Length, starts.Select(e => e.Event.Id).Distinct().Count());
    }

    private static string Text(IEnumerable<AIStreamEvent> events) => string.Concat(events.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(t => t.Delta));
    private static AIRequest Request() => new() { ProviderId = "manyfold", Model = "manyfold/agent", Input = new() { Text = "hello" } };
    private static string Completion(string text) => JsonSerializer.Serialize(new
    {
        id = "c1", model = "agent", choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } },
        usage = new { prompt_tokens = 1, completion_tokens = 2, total_tokens = 3 }, metadata = new { session_id = "s1" }
    }, Json);
    private static Fixture StreamFixture(IEnumerable<string> chunks) => new(_ => Response(string.Concat(chunks.Select(text => "data: " + JsonSerializer.Serialize(new
    {
        id = "c1", choices = new[] { new { index = 0, delta = new { content = text }, finish_reason = (string?)null } }, metadata = new { session_id = "s1" }
    }, Json) + "\n\n")) + "data: {\"id\":\"c1\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n"
        + "data: {\"id\":\"c1\",\"choices\":[],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":2,\"total_tokens\":3}}\n\ndata: [DONE]\n\n", "text/event-stream"));
    private static HttpResponseMessage Response(string body, string media = "application/json") => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, media) };
    private static async Task<List<AIStreamEvent>> Collect(ManyfoldProvider provider)
    {
        var events = new List<AIStreamEvent>();
        await foreach (var evt in provider.StreamUnifiedAsync(Request())) events.Add(evt);
        return events;
    }
    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly HttpClient client;
        public Handler Handler { get; }
        public ManyfoldProvider Provider { get; }
        public Fixture(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            Handler = new(responder); client = new(Handler);
            Provider = new(new Keys(), new AsyncCacheHelper(cache), new Factory(client));
        }
        public void Dispose() { client.Dispose(); cache.Dispose(); }
    }
    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => "test-key"; }
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return responder(request);
        }
    }
}
