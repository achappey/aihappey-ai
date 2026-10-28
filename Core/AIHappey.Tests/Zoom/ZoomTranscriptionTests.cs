using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using static AIHappey.Tests.Zoom.ZoomTestHelpers;

namespace AIHappey.Tests.Zoom;

public sealed class ZoomTranscriptionTests
{
    private const string ScribeResponse = """
        {"request_id":"req-scribe","duration_sec":3.24,"model":"zoom-asr-en-v1",
         "result":{"text_display":"Hello and welcome.","language":"en-US","segments":[
           {"text":"Hello and welcome.","start":0,"end":3.24,"speaker":"speaker_1","channel":0,
            "words":[{"word":"Hello","start":0,"end":0.2}]}]},
         "outputs":{"text_polishing":{"text":"Hello, and welcome."}}}
        """;

    [Fact]
    public async Task Vercel_audio_and_recognition_options_use_native_JSON_and_bearer_auth()
    {
        var provider = Provider(async (message, token) =>
        {
            Assert.Equal("https://api.zoom.us/v2/aiservices/scribe/transcribe", message.RequestUri!.ToString());
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.False(message.Headers.Contains("x-api-key"));
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal("AQID", payload.RootElement.GetProperty("file").GetString());
            var config = payload.RootElement.GetProperty("config");
            Assert.True(config.GetProperty("timestamps").GetBoolean());
            Assert.True(config.GetProperty("word_time_offsets").GetBoolean());
            Assert.True(config.GetProperty("diarization").GetBoolean());
            Assert.True(config.GetProperty("channel_separation").GetBoolean());
            Assert.True(config.GetProperty("profanity_filter").GetBoolean());
            Assert.Equal("zoom-scribe-multilingual", config.GetProperty("model").GetString());
            Assert.Equal("continuous", config.GetProperty("language_detection").GetProperty("mode").GetString());
            Assert.Equal("ace", config.GetProperty("vocabulary").GetProperty("phrases")[0].GetString());
            Assert.Equal("vocab-id", config.GetProperty("vocabulary_id").GetString());
            Assert.False(payload.RootElement.TryGetProperty("outputs", out _));
            return Reply(ScribeResponse);
        });
        var result = await provider.TranscriptionRequest(Read<TranscriptionRequest>("""
            {"model":"zoom/scribe","audio":"data:audio/wav;base64,AQID","mediaType":"audio/wav",
             "providerOptions":{"zoom":{"language":"en-US","timestamps":true,"word_time_offsets":true,
               "diarization":true,"channel_separation":true,"profanity_filter":true,
               "model":"zoom-scribe-multilingual","language_detection":{"mode":"continuous","candidate_languages":["en-US"]},
               "vocabulary":{"phrases":["ace"]},"vocabulary_id":"vocab-id"}}}
            """));
        Assert.Equal("Hello and welcome.", result.Text);
        Assert.Equal("en-US", result.Language);
        Assert.Equal(3.24f, result.DurationInSeconds);
        Assert.Equal(3.24f, Assert.Single(result.Segments).EndSecond);
        Assert.Equal("speaker_1", result.ProviderMetadata!["zoom"].GetProperty("result").GetProperty("segments")[0].GetProperty("speaker").GetString());
        Assert.Equal("Hello, and welcome.", result.ProviderMetadata["zoom"].GetProperty("outputs").GetProperty("text_polishing").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Text_polishing_is_forwarded_separately_from_ASR_config_and_preserved_in_metadata()
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.True(payload.RootElement.GetProperty("outputs").GetProperty("text_polishing").GetProperty("enabled").GetBoolean());
            Assert.False(payload.RootElement.GetProperty("config").TryGetProperty("outputs", out _));
            return Reply(ScribeResponse);
        });
        var result = await provider.TranscriptionRequest(Read<TranscriptionRequest>("""
            {"model":"scribe","audio":"AQID","mediaType":"audio/wav",
             "providerOptions":{"zoom":{"language":"en-US","outputs":{"text_polishing":{"enabled":true}}}}}
            """));
        Assert.Equal("Hello and welcome.", result.Text);
        Assert.Equal("Hello, and welcome.", result.ProviderMetadata!["zoom"].GetProperty("outputs").GetProperty("text_polishing").GetProperty("text").GetString());
    }

    [Fact]
    public async Task OpenAI_verbose_transcription_maps_segments_words_and_duration_usage()
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.True(payload.RootElement.GetProperty("config").GetProperty("word_time_offsets").GetBoolean());
            Assert.Equal("en-US", payload.RootElement.GetProperty("config").GetProperty("language").GetString());
            return Reply(ScribeResponse);
        });
        var request = OpenAIRequest();
        request.ResponseFormat = "verbose_json";
        request.TimestampGranularities = ["word", "segment"];
        var result = Assert.IsType<OpenAITranscriptionVerboseResponse>(await provider.OpenAITranscriptionRequestAsync(request));
        Assert.Equal("Hello and welcome.", Assert.Single(result.Segments!).Text);
        Assert.Equal("Hello", Assert.Single(result.Words!).Word);
        Assert.Equal(0.2, Assert.Single(result.Words!).End);
        Assert.Equal(3.24, Assert.IsType<OpenAITranscriptionDurationUsage>(result.Usage).Seconds);
    }

    [Fact]
    public async Task OpenAI_file_stream_is_buffered_then_emits_text_delta_and_done()
    {
        var provider = Provider((_, _) => Task.FromResult(Reply(ScribeResponse)));
        var events = new List<IOpenAITranscriptionStreamEvent>();
        await foreach (var part in provider.OpenAITranscriptionStreamingAsync(OpenAIRequest()))
            events.Add(part);
        Assert.Equal("Hello and welcome.", Assert.IsType<OpenAITranscriptionTextDelta>(events[0]).Delta);
        Assert.Equal("Hello and welcome.", Assert.IsType<OpenAITranscriptionTextDone>(events[1]).Text);
        Assert.Equal(3.24, Assert.IsType<OpenAITranscriptionDurationUsage>(
            Assert.IsType<OpenAITranscriptionTextDone>(events[1]).Usage).Seconds);
    }

    [Fact]
    public async Task Optional_recognition_model_and_response_model_can_be_omitted()
    {
        var provider = Provider(async (message, token) =>
        {
            using var payload = JsonDocument.Parse(await message.Content!.ReadAsStringAsync(token));
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("config").GetProperty("model").ValueKind);
            return Reply("""{"request_id":"req","duration_sec":1,"result":{"text_display":"Hello"}}""");
        });
        var result = await provider.TranscriptionRequest(Read<TranscriptionRequest>("""
            {"model":"zoom/scribe","audio":"AQID","mediaType":"audio/wav","providerOptions":{"zoom":{"model":null}}}
            """));
        Assert.Equal("Hello", result.Text);
        Assert.Equal("zoom/scribe", result.Response.ModelId);
    }

    [Theory]
    [InlineData("""{"model":"scribe","audio":"AQID","mediaType":"audio/aac"}""")]
    [InlineData("""{"model":"scribe","audio":"AQID","mediaType":"audio/wav","providerOptions":{"zoom":{"prompt":"ignored?"}}}""")]
    [InlineData("""{"model":"scribe","audio":"AQID","mediaType":"audio/wav","providerOptions":{"zoom":{"output_format":"srt"}}}""")]
    public async Task Unsupported_audio_options_are_not_silently_discarded(string json)
        => await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().TranscriptionRequest(Read<TranscriptionRequest>(json)));

    [Theory]
    [InlineData("not base64")]
    [InlineData("https://example.com/audio.wav")]
    [InlineData("data:audio/mp3;base64,AQID")]
    [InlineData("")]
    public async Task Invalid_audio_is_rejected_before_HTTP(string audio)
        => await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().TranscriptionRequest(new()
        {
            Model = "scribe", Audio = audio, MediaType = "audio/wav"
        }));

    [Fact]
    public async Task Unsupported_OpenAI_options_and_file_limit_fail_before_reading_upload()
    {
        var request = OpenAIRequest();
        request.Prompt = "unsupported";
        await Assert.ThrowsAsync<NotSupportedException>(() => NoHttp().OpenAITranscriptionRequestAsync(request));
        request = OpenAIRequest();
        request.File = new FormFile(Stream.Null, 0, 100_000_001, "file", "audio.wav");
        await Assert.ThrowsAsync<ArgumentException>(() => NoHttp().OpenAITranscriptionRequestAsync(request));
    }

    [Fact]
    public async Task Missing_credentials_fail_explicitly()
    {
        var provider = Provider((_, _) => throw new Xunit.Sdk.XunitException("Unexpected HTTP."), key: null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(new()
        {
            Model = "scribe", Audio = "AQID", MediaType = "audio/wav"
        }));
    }

    [Fact]
    public async Task Cancellation_propagates_on_all_operations_before_HTTP()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().TranscriptionRequest(new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().OpenAITranscriptionRequestAsync(new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().OpenAIDecisionRequestAsync(new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().DecisionRequestAsync(new(), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NoHttp().ExecuteUnifiedAsync(new AIRequest { ProviderId = "zoom" }, cancellation.Token));
    }

    [Fact]
    public async Task Inflight_cancellation_reaches_native_HTTP()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = Provider(async (_, token) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, token);
            return Reply("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.TranscriptionRequest(new()
        {
            Model = "scribe", Audio = "AQID", MediaType = "audio/wav"
        }, cancellation.Token));
    }

    [Theory]
    [InlineData("""{"request_id":"req","model":"asr","duration_sec":1,"result":{}}""")]
    [InlineData("""{"request_id":"req","model":"asr","duration_sec":-1,"result":{"text_display":"hi"}}""")]
    [InlineData("""{"request_id":"req","model":"asr","duration_sec":1,"result":{"text_display":"hi","segments":[{"text":"hi","start":2,"end":1}]}}""")]
    public async Task Malformed_Scribe_success_is_rejected(string body)
    {
        var provider = Provider((_, _) => Task.FromResult(Reply(body)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(new()
        {
            Model = "scribe", Audio = "AQID", MediaType = "audio/wav"
        }));
    }

    private static OpenAITranscriptionRequest OpenAIRequest() => new()
    {
        Model = "zoom/scribe",
        Language = "en-US",
        File = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        }
    };
}
