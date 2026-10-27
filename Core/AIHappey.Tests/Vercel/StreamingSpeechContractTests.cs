using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class StreamingSpeechContractTests
{
    [Fact]
    public void Request_round_trip_preserves_part_boundaries_voices_and_provider_options()
    {
        const string json = """
            {
              "model": "example/tts",
              "text": [
                {"type":"text","text":"hel","voice":"alice","providerOptions":{"example":{"newTurn":true}}},
                {"type":"text","text":"lo ","voice":"bob"},
                {"type":"text","text":"🌍"},
                {"type":"text","text":""}
              ],
              "voice": "default",
              "outputFormat": {"type":"audio/pcm","rate":24000},
              "instructions": "Speak calmly",
              "speed": 1.125,
              "language": "en",
              "providerOptions": {"example":{"seed":42,"custom":[1,true,null]}},
              "includeRawChunks": true
            }
            """;

        var request = JsonSerializer.Deserialize<StreamingSpeechRequest>(json)!;
        var serialized = JsonSerializer.SerializeToElement(request);
        var roundTrip = JsonSerializer.Deserialize<StreamingSpeechRequest>(serialized)!;

        Assert.Equal("example/tts", roundTrip.Model);
        Assert.Equal(["hel", "lo ", "🌍", ""], roundTrip.Text.Select(part => part.Text).ToArray());
        Assert.All(roundTrip.Text, part => Assert.Equal("text", part.Type));
        Assert.Equal("alice", roundTrip.Text[0].Voice);
        Assert.Equal("bob", roundTrip.Text[1].Voice);
        Assert.Null(roundTrip.Text[2].Voice);
        Assert.True(roundTrip.Text[0].ProviderOptions!["example"].GetProperty("newTurn").GetBoolean());
        Assert.Equal("default", roundTrip.Voice);
        Assert.Equal("audio/pcm", roundTrip.OutputFormat!.Type);
        Assert.Equal(24000, roundTrip.OutputFormat.Rate);
        Assert.Equal("Speak calmly", roundTrip.Instructions);
        Assert.Equal(1.125, roundTrip.Speed);
        Assert.Equal("en", roundTrip.Language);
        Assert.True(roundTrip.IncludeRawChunks);
        Assert.Equal(42, roundTrip.ProviderOptions!["example"].GetProperty("seed").GetInt32());
        Assert.Equal(JsonValueKind.Null, roundTrip.ProviderOptions["example"].GetProperty("custom")[2].ValueKind);
        Assert.Equal(JsonValueKind.Array, serialized.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.Object, serialized.GetProperty("outputFormat").ValueKind);
    }

    [Fact]
    public void Minimal_request_keeps_optional_settings_unset()
    {
        var request = JsonSerializer.Deserialize<StreamingSpeechRequest>(
            """{"model":"example/tts","text":[]}""")!;

        Assert.Empty(request.Text);
        Assert.Null(request.Voice);
        Assert.Null(request.OutputFormat);
        Assert.Null(request.Instructions);
        Assert.Null(request.Speed);
        Assert.Null(request.Language);
        Assert.Null(request.ProviderOptions);
        Assert.Null(request.IncludeRawChunks);
    }

    [Theory]
    [InlineData("text", "", true)]
    [InlineData("text", " ", true)]
    [InlineData("text", "hel", true)]
    [InlineData("text", null, false)]
    [InlineData("audio", "hello", false)]
    [InlineData("", "hello", false)]
    public void Text_parts_validate_type_and_allow_empty_or_partial_text(string type, string? text, bool valid)
    {
        var part = new StreamingSpeechTextPart { Type = type, Text = text! };
        Assert.Equal(valid, Validator.TryValidateObject(part, new ValidationContext(part), [], true));
    }

    public static IEnumerable<object[]> EventCases()
    {
        yield return [new SpeechStreamStartPart
        {
            Warnings = [new { type = "unsupported", feature = "speed", details = "Not supported" }]
        }, "stream-start", "warnings"];
        yield return [new SpeechAudioPart
        {
            Id = "chunk-1", Audio = "AQID", AudioFormat = new AudioFormat { Type = "audio/pcm", Rate = 24000 },
            ProviderMetadata = Metadata()
        }, "audio", "audioFormat"];
        yield return [new SpeechResponseMetadataPart
        {
            Timestamp = new DateTime(2026, 10, 27, 13, 0, 0, DateTimeKind.Utc), ModelId = "tts",
            Headers = new Dictionary<string, string> { ["x-request-id"] = "request-1" },
            Body = new { requestId = "request-1" }
        }, "response-metadata", "modelId"];
        yield return [new SpeechFinishPart
        {
            Usage = new StreamingSpeechUsage
            {
                InputCharacters = 5, InputTokens = 2, OutputTokens = 20,
                Raw = new Dictionary<string, JsonElement> { ["billed"] = JsonSerializer.SerializeToElement(12) }
            },
            ProviderMetadata = Metadata()
        }, "finish", "usage"];
        yield return [new SpeechRawPart { RawValue = new { nested = new[] { 1, 2 } } }, "raw", "rawValue"];
        yield return [new SpeechErrorPart { Error = new { code = "warning", message = "Recoverable" } }, "error", "error"];
    }

    [Theory]
    [MemberData(nameof(EventCases))]
    public void All_events_serialize_with_discriminators_and_round_trip(StreamingSpeechPart part, string type, string field)
    {
        // Serialize through the same base type used by both SSE controllers.
        var json = JsonSerializer.SerializeToElement(part);
        var roundTrip = JsonSerializer.Deserialize<StreamingSpeechPart>(json);

        Assert.Equal(type, json.GetProperty("type").GetString());
        Assert.True(json.TryGetProperty(field, out _));
        Assert.IsType(part.GetType(), roundTrip);
        Assert.Equal(json.GetRawText(), JsonSerializer.Serialize<StreamingSpeechPart>(roundTrip!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AQID")]
    public void Audio_is_plain_base64_and_empty_metadata_only_chunks_are_preserved(string audio)
    {
        StreamingSpeechPart part = new SpeechAudioPart
        {
            Audio = audio, AudioFormat = new AudioFormat { Type = "audio/mpeg" }, ProviderMetadata = Metadata()
        };

        var json = JsonSerializer.SerializeToElement(part);
        var decoded = Assert.IsType<SpeechAudioPart>(JsonSerializer.Deserialize<StreamingSpeechPart>(json));

        Assert.Equal(audio, json.GetProperty("audio").GetString());
        Assert.Equal(Convert.FromBase64String(audio), Convert.FromBase64String(decoded.Audio));
        Assert.Equal("audio/mpeg", decoded.AudioFormat.Type);
        Assert.Null(decoded.AudioFormat.Rate);
        Assert.True(decoded.ProviderMetadata!["example"].GetProperty("endOfTurn").GetBoolean());
    }

    [Fact]
    public void Finish_usage_and_metadata_are_optional_and_usage_counters_are_independent()
    {
        var finish = Assert.IsType<SpeechFinishPart>(JsonSerializer.Deserialize<StreamingSpeechPart>("""{"type":"finish"}"""));
        var usage = JsonSerializer.Deserialize<StreamingSpeechUsage>("""{"inputCharacters":5000000000}""")!;

        Assert.Null(finish.Usage);
        Assert.Null(finish.ProviderMetadata);
        Assert.Equal(5000000000L, usage.InputCharacters);
        Assert.Null(usage.InputTokens);
        Assert.Null(usage.OutputTokens);
        Assert.Null(usage.Raw);
    }

    [Fact]
    public void Unknown_event_discriminators_are_rejected()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<StreamingSpeechPart>("""{"type":"unknown"}"""));

    private static Dictionary<string, JsonElement> Metadata() => new()
    {
        ["example"] = JsonSerializer.SerializeToElement(new { endOfTurn = true, alignment = new[] { 0.1, 0.2 } })
    };
}
