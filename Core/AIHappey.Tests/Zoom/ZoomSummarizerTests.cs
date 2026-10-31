using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using static AIHappey.Tests.Zoom.ZoomTestHelpers;

namespace AIHappey.Tests.Zoom;

public sealed class ZoomSummarizerTests
{
    private const string SummaryResponse = """
        {"request_id":"req-summary","task":"full_summary","result":{"full_summary":"## Recap\nBob will document the API."},
         "model":"zoom-summarizer-v1","usage":{"input_units":4820,"output_units":312,"unit_type":"characters",
         "details":{"summarizer":{"task":"full_summary","summary_type":"CONVERSATION"}}}}
        """;

    [Fact]
    public async Task Responses_string_input_uses_native_Summarizer_and_returns_Markdown_not_JSON()
    {
        var provider = Provider(async (message, token) =>
        {
            Assert.Equal("https://api.zoom.us/v2/aiservices/summarizer/summarize", message.RequestUri!.ToString());
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("zoom-secret", message.Headers.Authorization.Parameter);
            Assert.False(message.Headers.Contains("x-api-key"));
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("Alice: Document the API.\nBob: I will.", payload.RootElement.GetProperty("input").GetProperty("text").GetString());
            Assert.Equal("full_summary", payload.RootElement.GetProperty("config").GetProperty("task").GetString());
            Assert.Equal("CONVERSATION", payload.RootElement.GetProperty("config").GetProperty("summary_type").GetString());
            Assert.Equal("text", payload.RootElement.GetProperty("config").GetProperty("output_format").GetString());
            Assert.False(payload.RootElement.TryGetProperty("model", out _));
            return Reply(SummaryResponse);
        });
        var result = await provider.ResponsesAsync(Read<ResponseRequest>("""
            {"model":"zoom/summarizer","input":"Alice: Document the API.\nBob: I will."}
            """));
        var json = JsonSerializer.SerializeToElement(result, JsonSerializerOptions.Web);
        Assert.Equal("## Recap\nBob will document the API.", json.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("zoom/summarizer", json.GetProperty("model").GetString());
        Assert.Equal(4820, json.GetProperty("metadata").GetProperty("zoom").GetProperty("usage").GetProperty("input_units").GetInt32());
        Assert.True(!json.TryGetProperty("usage", out var usage) || usage.ValueKind == JsonValueKind.Null
            || usage.GetProperty("input_tokens").GetInt32() == 0);
    }

    [Theory]
    [InlineData("recap", "recap")]
    [InlineData("summary", "summary_text")]
    [InlineData("action_items", "action_items")]
    [InlineData("full_summary", "full_summary")]
    public async Task Task_options_and_multiple_native_text_parts_are_mapped(string task, string resultField)
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("Line one.\nLine two.", payload.RootElement.GetProperty("input").GetProperty("text").GetString());
            var config = payload.RootElement.GetProperty("config");
            Assert.Equal(task, config.GetProperty("task").GetString());
            Assert.Equal("fr-FR", config.GetProperty("language").GetString());
            Assert.Equal("trace", payload.RootElement.GetProperty("reference_id").GetString());
            var body = JsonNode.Parse(SummaryResponse)!;
            body["task"] = task;
            body["result"] = new JsonObject { [resultField] = "**Rendered output**" };
            return Reply(body.ToJsonString());
        });
        var request = Read<ResponseRequest>("""
            {"model":"summarizer","input":[{"role":"user","content":[
              {"type":"input_text","text":"Line one."},{"type":"input_text","text":"Line two."}]}]}
            """);
        request.Metadata = new() { ["zoom"] = new { task, language = "fr-FR", reference_id = "trace" } };
        var result = await provider.ResponsesAsync(request);
        var json = JsonSerializer.SerializeToElement(result);
        Assert.Equal("**Rendered output**", json.GetProperty("output")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Vercel_stream_extracts_latest_user_text_and_keeps_native_character_usage()
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("New transcript.", payload.RootElement.GetProperty("input").GetProperty("text").GetString());
            return Reply(SummaryResponse);
        });
        var request = Read<ChatRequest>("""
            {"model":"zoom/summarizer","messages":[
              {"id":"old","role":"user","parts":[{"type":"text","text":"Old transcript."}]},
              {"id":"reply","role":"assistant","parts":[{"type":"text","text":"Old summary."}]},
              {"id":"new","role":"user","parts":[{"type":"text","text":"New transcript."}]}],
              "providerMetadata":{"zoom":{"task":"full_summary"}}}
            """);
        var parts = new List<JsonElement>();
        await foreach (var part in provider.StreamAsync(request))
            parts.Add(JsonSerializer.SerializeToElement(part, JsonSerializerOptions.Web));
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "text-start");
        var delta = Assert.Single(parts, part => part.GetProperty("type").GetString() == "text-delta");
        Assert.Equal("## Recap\nBob will document the API.", delta.GetProperty("delta").GetString());
        Assert.Equal("characters", delta.GetProperty("providerMetadata").GetProperty("zoom").GetProperty("usage").GetProperty("unit_type").GetString());
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "text-end");
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "finish");
    }

    [Fact]
    public async Task Responses_stream_has_regular_text_and_completed_response()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply(SummaryResponse)));
        var parts = new List<JsonElement>();
        await foreach (var part in provider.ResponsesStreamingAsync(Read<ResponseRequest>("""
            {"model":"zoom/summarizer","input":"Transcript","stream":true}
            """)))
            parts.Add(JsonSerializer.SerializeToElement(part, part.GetType(), JsonSerializerOptions.Web));
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "response.output_text.delta");
        Assert.Contains(parts, part => part.GetProperty("type").GetString() == "response.completed");
    }

    [Theory]
    [InlineData("""{"model":"zoom/summarizer","input":[{"role":"user","content":[{"type":"input_image","image_url":"https://example.com/image.png"}]}]}""")]
    [InlineData("""{"model":"zoom/summarizer","input":"text","instructions":"Be brief"}""")]
    [InlineData("""{"model":"zoom/summarizer","input":"text","temperature":0.5}""")]
    [InlineData("""{"model":"zoom/summarizer","input":"text","previous_response_id":"resp_123"}""")]
    [InlineData("""{"model":"zoom/summarizer","input":"text","reasoning":{"effort":"high"}}""")]
    [InlineData("""{"model":"zoom/zoom-decisions","input":"text"}""")]
    public async Task Unsupported_native_inputs_fail_explicitly(string json)
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().ResponsesAsync(Read<ResponseRequest>(json)));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in NoHttp().ResponsesStreamingAsync(Read<ResponseRequest>(json)))
            {
                Assert.Fail("Unsupported input must not produce stream events.");
            }
        });
    }

    [Fact]
    public async Task Transcript_limit_uses_UTF8_bytes_and_empty_input_is_rejected()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "zoom", Model = "summarizer", Input = new() { Text = new string('\u00e9', 49_153) }
        }));
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "zoom", Model = "summarizer", Input = new() { Text = "  " }
        }));
    }

    [Fact]
    public async Task Transcript_at_exactly_96_KB_is_accepted()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply(SummaryResponse)));
        var response = await provider.ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "zoom", Model = "summarizer", Input = new() { Text = new string('\u00e9', 49_152) }
        });
        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Cancellation_between_summary_text_events_is_observed()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Provider((_, _) => Task.FromResult(Reply(SummaryResponse)));
        await using var stream = provider.StreamUnifiedAsync(new AIRequest
        {
            ProviderId = "zoom", Model = "summarizer", Input = new() { Text = "Transcript" }
        }, cancellation.Token).GetAsyncEnumerator();
        Assert.True(await stream.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await stream.MoveNextAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("task")]
    [InlineData("usage")]
    [InlineData("json")]
    public async Task Malformed_success_is_rejected(string invalid)
    {
        var body = JsonNode.Parse(SummaryResponse)!;
        if (invalid == "missing") body["result"] = new JsonObject();
        if (invalid == "task") body["task"] = "recap";
        if (invalid == "usage") body["usage"]!["unit_type"] = "tokens";
        var provider = Provider((_, _) => Task.FromResult(Reply(invalid == "json" ? "not json" : body.ToJsonString())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ResponsesAsync(Read<ResponseRequest>("""
            {"model":"summarizer","input":"Transcript"}
            """)));
    }
}
