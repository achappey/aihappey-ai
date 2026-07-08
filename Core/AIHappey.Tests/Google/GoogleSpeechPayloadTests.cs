using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Providers.Google;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Google;

public sealed class GoogleSpeechPayloadTests
{
    [Fact]
    public void BuildSpeechPayloadWithoutProviderOptionsUsesGeneralFieldsAndDefaultVoice()
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview",
            Text = "Say cheerfully: Have a wonderful day!"
        });

        Assert.Equal("gemini-3.1-flash-tts-preview", payload.GetProperty("model").GetString());
        Assert.Equal("Say cheerfully: Have a wonderful day!", payload.GetProperty("input").GetString());
        Assert.Equal("audio", payload.GetProperty("response_format").GetProperty("type").GetString());

        var speechConfig = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single();
        Assert.Equal("Kore", speechConfig.GetProperty("voice").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildSpeechPayloadMapsGeneralVoice()
    {
        var (payload, _) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview",
            Text = "Hello",
            Voice = "Puck"
        });

        var speechConfig = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single();
        Assert.Equal("Puck", speechConfig.GetProperty("voice").GetString());
    }

    [Fact]
    public void BuildSpeechPayloadLetsRawProviderOptionsOverrideDefaults()
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview",
            Text = "Hello",
            Voice = "Kore",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    model = "custom-model",
                    input = "custom input",
                    response_format = new { type = "audio" },
                    generation_config = new
                    {
                        speech_config = new object[]
                        {
                            new { speaker = "Jane", voice = "Puck" }
                        },
                        seed = 123
                    },
                    stream = false
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.Equal("custom-model", payload.GetProperty("model").GetString());
        Assert.Equal("custom input", payload.GetProperty("input").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.Equal(123, payload.GetProperty("generation_config").GetProperty("seed").GetInt32());

        var speechConfig = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single();
        Assert.Equal("Jane", speechConfig.GetProperty("speaker").GetString());
        Assert.Equal("Puck", speechConfig.GetProperty("voice").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildSpeechPayloadMovesTopLevelRawSpeechConfigIntoGenerationConfig()
    {
        var (payload, _) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview",
            Text = "Hello",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["google"] = JsonSerializer.SerializeToElement(new
                {
                    speech_config = new object[]
                    {
                        new { speaker = "Joe", voice = "Charon" },
                        new { speaker = "Jane", voice = "Puck" }
                    }
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.False(payload.TryGetProperty("speech_config", out _));
        var configs = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().ToList();

        Assert.Equal(2, configs.Count);
        Assert.Equal("Joe", configs[0].GetProperty("speaker").GetString());
        Assert.Equal("Charon", configs[0].GetProperty("voice").GetString());
    }

    [Fact]
    public void TryExtractGoogleSpeechAudioReadsOutputAudioData()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            output_audio = new
            {
                data = "audio-base64"
            }
        }, JsonSerializerOptions.Web);

        var (found, base64, mimeType) = ExtractAudio(json);

        Assert.True(found);
        Assert.Equal("audio-base64", base64);
        Assert.Null(mimeType);
    }

    [Fact]
    public void TryExtractGoogleSpeechAudioFallsBackToNestedAudioContent()
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            steps = new object[]
            {
                new
                {
                    type = "model_output",
                    content = new object[]
                    {
                        new
                        {
                            type = "audio",
                            data = "nested-audio-base64",
                            mime_type = "audio/wav"
                        }
                    }
                }
            }
        }, JsonSerializerOptions.Web);

        var (found, base64, mimeType) = ExtractAudio(json);

        Assert.True(found);
        Assert.Equal("nested-audio-base64", base64);
        Assert.Equal("audio/wav", mimeType);
    }

    private static (JsonElement Payload, List<object> Warnings) BuildPayload(SpeechRequest request)
    {
        var warnings = new List<object>();
        var method = typeof(GoogleAIProvider).GetMethod("BuildSpeechPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "BuildSpeechPayload");

        try
        {
            var payload = (JsonObject)method.Invoke(null, [request, warnings])!;
            return (JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), warnings);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static (bool Found, string Base64, string? MimeType) ExtractAudio(JsonElement root)
    {
        var method = typeof(GoogleAIProvider).GetMethod("TryExtractGoogleSpeechAudio", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "TryExtractGoogleSpeechAudio");

        var parameters = new object?[] { root, null, null };
        var found = (bool)method.Invoke(null, parameters)!;

        return (found, (string)parameters[1]!, (string?)parameters[2]);
    }
}
