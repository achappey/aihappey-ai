using System.Text;
using System.Text.Json;
using AIHappey.Unified.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.EdgeSpeak;

public sealed partial class EdgeSpeakProviderTests
{
    [Fact]
    public async Task Unified_speech_forwards_real_audio_resources_immediately_without_accumulating_a_clip()
    {
        var stream = new PushStream();
        stream.Push(Delta);
        var provider = Create(async (message, _) =>
        {
            if (message.Method == System.Net.Http.HttpMethod.Get)
                return Json(Catalog);
            var body = await Payload(message);
            Assert.Equal(Model, body.GetProperty("model").GetString());
            Assert.Equal("builtin:auto", body.GetProperty("voice").GetString());
            Assert.Equal("sse", body.GetProperty("stream_format").GetString());
            Assert.Equal("Hello", body.GetProperty("input").GetString());
            Assert.Equal(42, body.GetProperty("seed").GetInt32());
            return Sse(stream);
        });
        await using var iterator = provider.StreamUnifiedAsync(new()
        {
            ProviderId = "edgespeak", Model = "edgespeak/" + Model,
            Input = new() { Text = "Hello" }, Metadata = new() { ["edgespeak"] = new { voice = "builtin:auto", seed = 42 } }
        }).GetAsyncEnumerator();
        Assert.True(await Next(iterator));
        Assert.IsType<AIToolInputAvailableEventData>(iterator.Current.Event.Data);
        Assert.True(await Next(iterator));
        var delta = Assert.IsType<AIToolOutputAvailableEventData>(iterator.Current.Event.Data);
        Assert.True(delta.Preliminary);
        var result = Assert.IsType<CallToolResult>(delta.Output);
        var resource = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(Assert.Single(result.Content)).Resource);
        Assert.Equal("UklGRg==", Encoding.UTF8.GetString(resource.Blob.Span));
        Assert.Equal("audio/wav", resource.MimeType);
        Assert.False(stream.Disposed);
        stream.Push(Delta + Done);
        Assert.True(await Next(iterator));
        var second = Assert.IsType<AIToolOutputAvailableEventData>(iterator.Current.Event.Data);
        Assert.Single(Assert.IsType<CallToolResult>(second.Output).Content);
        Assert.True(await Next(iterator));
        var complete = Assert.IsType<AIToolOutputAvailableEventData>(iterator.Current.Event.Data);
        Assert.False(complete.Preliminary);
        Assert.Empty(Assert.IsType<CallToolResult>(complete.Output).Content);
        Assert.True(await Next(iterator));
        Assert.IsType<AIFinishEventData>(iterator.Current.Event.Data);
        Assert.False(await Next(iterator));
        Assert.True(stream.Disposed);
    }

    [Fact]
    public async Task Unified_buffered_speech_uses_native_options_and_model_voice_shortcuts()
    {
        var provider = Create(async (message, _) =>
        {
            if (message.Method == System.Net.Http.HttpMethod.Get)
                return Json(Catalog);
            var body = await Payload(message);
            Assert.Equal("builtin:bright-girl", body.GetProperty("voice").GetString());
            Assert.Equal("en-US", body.GetProperty("language").GetString());
            Assert.Equal("Calm", body.GetProperty("instructions").GetString());
            Assert.Equal("wav", body.GetProperty("response_format").GetString());
            return Binary();
        });
        var response = await provider.ExecuteUnifiedAsync(new()
        {
            ProviderId = "edgespeak", Model = "edgespeak/" + Model + "/builtin:bright-girl",
            Input = new() { Text = "Hello" }, Instructions = "Calm",
            Metadata = new() { ["edgespeak"] = new { language = "en-US" } }
        });
        var tool = Assert.IsType<AIToolCallContentPart>(Assert.Single(response.Output!.Items![0].Content!));
        Assert.Single(Assert.IsType<CallToolResult>(tool.Output).Content);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unified_transcription_preserves_provider_options_and_does_not_duplicate_done_text(bool streaming)
    {
        var provider = Create(async (message, ct) =>
        {
            if (message.Method == System.Net.Http.HttpMethod.Get)
                return Json(Catalog);
            var form = await ReadForm(message, ct);
            Assert.Equal("fr", form["language"]);
            Assert.Equal("Context", form["prompt"]);
            return streaming ? Sse(TranscriptDelta + TranscriptDone) : Json(Transcript);
        });
        var request = new AIRequest
        {
            ProviderId = "edgespeak", Model = "transcriber/model", Instructions = "Context",
            Metadata = new() { ["edgespeak"] = JsonSerializer.SerializeToElement(new { language = "fr" }) },
            Input = new() { Items = [new()
            {
                Role = "user", Content = [new AIFileContentPart { Type = "file", MediaType = "audio/wav", Data = "UklGRg==" }]
            }] }
        };
        if (streaming)
        {
            var events = await Drain(provider.StreamUnifiedAsync(request));
            Assert.Equal("Hello", Assert.Single(events.Select(p => p.Event.Data).OfType<AITextDeltaEventData>()).Delta);
            Assert.Single(events.Select(p => p.Event.Data).OfType<AIFinishEventData>());
        }
        else
        {
            var response = await provider.ExecuteUnifiedAsync(request);
            Assert.Equal("Hello world", Assert.IsType<AITextContentPart>(Assert.Single(response.Output!.Items![0].Content!)).Text);
        }
    }
}
