using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Messages;
using AIHappey.Responses;

namespace AIHappey.Tests.EdgeSpeak;

public sealed partial class EdgeSpeakProviderTests
{
    [Fact]
    public async Task Chat_and_vision_forward_standard_and_native_generation_options()
    {
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/chat/completions", message.RequestUri!.AbsolutePath);
            var body = await Payload(message);
            Assert.Equal("Qwen/Qwen3.5-4B", body.GetProperty("model").GetString());
            Assert.Equal(0.7, body.GetProperty("top_p").GetDouble());
            Assert.True(body.GetProperty("enable_thinking").GetBoolean());
            Assert.Equal("image_url", body.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("type").GetString());
            return Json("""
                {"id":"chat-1","object":"chat.completion","created":1,"model":"Qwen/Qwen3.5-4B",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"Hello"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":2,"completion_tokens":1,"total_tokens":3}}
                """);
        });
        var response = await provider.CompleteChatAsync(new()
        {
            Model = "Qwen/Qwen3.5-4B",
            Messages = [new()
            {
                Role = "user", Content = JsonSerializer.SerializeToElement(new object[]
                {
                    new { type = "text", text = "Describe" },
                    new { type = "image_url", image_url = new { url = "data:image/png;base64,AQID" } }
                })
            }],
            Metadata = new() { ["edgespeak"] = new { top_p = 0.7, enable_thinking = true } }
        });
        Assert.Equal("edgespeak/Qwen/Qwen3.5-4B", response.Model);
    }

    [Fact]
    public async Task Chat_streaming_uses_native_SSE_and_preserves_deltas()
    {
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.True((await Payload(message)).GetProperty("stream").GetBoolean());
            return Sse("""
                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Qwen/Qwen3.5-4B","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":null}]}

                data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"Qwen/Qwen3.5-4B","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                data: [DONE]

                """);
        });
        var updates = await Drain(provider.CompleteChatStreamingAsync(new() { Model = "Qwen/Qwen3.5-4B", Stream = true }));
        Assert.Equal(2, updates.Count);
        Assert.Contains("Hello", JsonSerializer.Serialize(updates[0]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Responses_use_the_native_endpoint(bool streaming)
    {
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/responses", message.RequestUri!.AbsolutePath);
            Assert.Equal("Qwen/Qwen3.5-4B", (await Payload(message)).GetProperty("model").GetString());
            return streaming ? Sse("""
                event: response.created
                data: {"type":"response.created","response":{"id":"resp-1","object":"response","model":"Qwen/Qwen3.5-4B","status":"in_progress","output":[]}}

                event: response.output_text.delta
                data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"content_index":0,"delta":"Hello"}

                event: response.completed
                data: {"type":"response.completed","response":{"id":"resp-1","object":"response","model":"Qwen/Qwen3.5-4B","status":"completed","output":[]}}

                """) : Json("""{"id":"resp-1","object":"response","model":"Qwen/Qwen3.5-4B","status":"completed","output":[]}""");
        });
        var request = new ResponseRequest { Model = "Qwen/Qwen3.5-4B", Stream = streaming };
        if (streaming)
            Assert.NotEmpty(await Drain(provider.ResponsesStreamingAsync(request)));
        else
            Assert.NotNull(await provider.ResponsesAsync(request));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Messages_use_the_native_endpoint(bool streaming)
    {
        var provider = Create(async (message, _) =>
        {
            AssertAuth(message);
            Assert.Equal("/v1/messages", message.RequestUri!.AbsolutePath);
            Assert.Equal("Qwen/Qwen3.5-4B", (await Payload(message)).GetProperty("model").GetString());
            return streaming ? Sse("""
                event: message_start
                data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","model":"Qwen/Qwen3.5-4B","content":[],"usage":{"input_tokens":2,"output_tokens":0}}}

                event: content_block_delta
                data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hello"}}

                event: message_stop
                data: {"type":"message_stop"}

                """) : Json("""
                {"id":"msg-1","type":"message","role":"assistant","model":"Qwen/Qwen3.5-4B",
                 "content":[{"type":"text","text":"Hello"}],"stop_reason":"end_turn","usage":{"input_tokens":2,"output_tokens":1}}
                """);
        });
        var request = JsonSerializer.Deserialize<MessagesRequest>(
            """{"model":"Qwen/Qwen3.5-4B","max_tokens":32,"messages":[{"role":"user","content":"Hi"}]}""")!;
        request.Stream = streaming;
        if (streaming)
            Assert.Equal(3, (await Drain(provider.MessagesStreamingAsync(request, []))).Count);
        else
            Assert.Equal("edgespeak/Qwen/Qwen3.5-4B", (await provider.MessagesAsync(request, [])).Model);
    }
}
