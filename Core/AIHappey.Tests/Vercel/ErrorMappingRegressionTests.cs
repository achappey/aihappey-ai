using System.Text.Json;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

// Backend translation coverage formerly coupled the desktop protocol tests to Unified.
public sealed class ErrorMappingRegressionTests
{
    private const string Exact = "Unsupported parameter: reasoning.effort is not supported with this model.";

    [Fact]
    public void Unified_error_preserves_provider_message_in_vercel_wire()
    {
        var envelope = new AIEventEnvelope { Type = "error", Data = new AIErrorEventData { ErrorText = Exact } };

        var part = Assert.IsType<ErrorUIPart>(Assert.Single(envelope.ToUIMessagePart("openai")));

        Assert.Equal(Exact, part.ErrorText);
        var wire = JsonSerializer.SerializeToElement<UIMessagePart>(part, JsonSerializerOptions.Web);
        Assert.Equal("error", wire.GetProperty("type").GetString());
        Assert.Equal(Exact, wire.GetProperty("errorText").GetString());
    }

    [Theory]
    [InlineData("error")]
    [InlineData("response.failed")]
    public void Responses_error_preserves_provider_message_in_unified_event(string type)
    {
        var error = new { message = Exact, code = "invalid_request_error" };
        var json = type == "error"
            ? JsonSerializer.Serialize(new { type, error })
            : JsonSerializer.Serialize(new { type, response = new { id = "response", status = "failed", model = "gpt-6-luna", output = Array.Empty<object>(), error } });
        var part = JsonSerializer.Deserialize<ResponseStreamPart>(json)!;

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent("openai"));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal(Exact, Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
    }

    [Fact]
    public void Responses_top_level_error_preserves_provider_message_in_unified_event_and_vercel_wire()
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "error",
            message = Exact,
            code = "invalid_request_error",
            param = "reasoning.effort",
            sequence_number = 1
        });
        var part = JsonSerializer.Deserialize<ResponseStreamPart>(json)!;

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent("openai"));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal(Exact, Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
        var uiPart = Assert.IsType<ErrorUIPart>(Assert.Single(streamEvent.Event.ToUIMessagePart("openai")));
        var wire = JsonSerializer.SerializeToElement<UIMessagePart>(uiPart, JsonSerializerOptions.Web);
        Assert.Equal("error", wire.GetProperty("type").GetString());
        Assert.Equal(Exact, wire.GetProperty("errorText").GetString());
    }

    [Fact]
    public void Responses_error_prefers_top_level_message_without_trimming()
    {
        var message = $"  {Exact}\r\n";
        var json = JsonSerializer.Serialize(new
        {
            type = "error",
            message,
            error = new { message = "Different nested message" }
        });
        var part = JsonSerializer.Deserialize<ResponseStreamPart>(json)!;

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent("openai"));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal(message, Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void Responses_error_uses_nested_message_when_top_level_message_is_blank(string? message)
    {
        var nestedMessage = $"  {Exact}\r\n";
        var json = JsonSerializer.Serialize(new
        {
            type = "error",
            message,
            error = new { message = nestedMessage, code = "invalid_request_error" }
        });
        var part = JsonSerializer.Deserialize<ResponseStreamPart>(json)!;

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent("openai"));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal(nestedMessage, Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
        var uiPart = Assert.IsType<ErrorUIPart>(Assert.Single(streamEvent.Event.ToUIMessagePart("openai")));
        var wire = JsonSerializer.SerializeToElement<UIMessagePart>(uiPart, JsonSerializerOptions.Web);
        Assert.Equal(nestedMessage, wire.GetProperty("errorText").GetString());
    }

    [Theory]
    [InlineData("""{"type":"error"}""")]
    [InlineData("""{"type":"error","message":null}""")]
    [InlineData("""{"type":"error","message":""}""")]
    [InlineData("""{"type":"error","message":" \t\r\n"}""")]
    [InlineData("""{"type":"error","error":null}""")]
    [InlineData("""{"type":"error","error":"not an object"}""")]
    [InlineData("""{"type":"error","error":42}""")]
    [InlineData("""{"type":"error","error":[]}""")]
    [InlineData("""{"type":"error","error":{}}""")]
    [InlineData("""{"type":"error","error":{"message":null}}""")]
    [InlineData("""{"type":"error","error":{"message":""}}""")]
    [InlineData("""{"type":"error","error":{"message":" \t\r\n"}}""")]
    [InlineData("""{"type":"error","error":{"message":42}}""")]
    [InlineData("""{"type":"error","error":{"message":true}}""")]
    [InlineData("""{"type":"error","error":{"message":[]}}""")]
    [InlineData("""{"type":"error","error":{"message":{}}}""")]
    public void Responses_error_without_usable_message_preserves_existing_fallback(string json)
    {
        var part = JsonSerializer.Deserialize<ResponseStreamPart>(json)!;

        var streamEvent = Assert.Single(part.ToUnifiedStreamEvent("openai"));

        Assert.Equal("error", streamEvent.Event.Type);
        Assert.Equal("The provider returned an error event without an error message.",
            Assert.IsType<AIErrorEventData>(streamEvent.Event.Data).ErrorText);
    }
}
