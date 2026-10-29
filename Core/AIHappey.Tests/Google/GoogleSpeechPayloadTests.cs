using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Models;
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

    [Theory]
    [InlineData("gemini-3.8-flash-tts")]
    [InlineData("google/gemini-3.8-flash-lite-tts/Puck")]
    public void BuildSpeechPayloadMapsInstructionsToSpeechMetadataWithoutChangingTranscript(string model)
    {
        const string text = "  Wait... <short pause> did you hear that?\n<sigh> Have a wonderful day!  ";
        const string instructions = "  whispered urgently, then cheerful and friendly\nwith a gentle pace  ";
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = model,
            Text = text,
            Instructions = instructions
        });

        var input = Assert.Single(payload.GetProperty("input").EnumerateArray());
        Assert.Equal("user_input", input.GetProperty("type").GetString());
        var content = Assert.Single(input.GetProperty("content").EnumerateArray());
        Assert.Equal("text", content.GetProperty("type").GetString());
        Assert.Equal(text, content.GetProperty("text").GetString());
        var annotation = Assert.Single(content.GetProperty("annotations").EnumerateArray());
        Assert.Equal("speech_metadata", annotation.GetProperty("type").GetString());
        Assert.Equal(instructions, annotation.GetProperty("style").GetString());
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void BuildSpeechPayloadKeepsPlainInputForBlankInstructions(string? instructions)
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.8-flash-tts",
            Text = "Hello <laugh>",
            Instructions = instructions
        });

        Assert.Equal("Hello <laugh>", payload.GetProperty("input").GetString());
        Assert.Empty(warnings);
    }

    [Theory]
    [InlineData("input", "\"native transcript\"")]
    [InlineData("INPUT", "\"native transcript\"")]
    [InlineData("input", "null")]
    [InlineData("input", "[{\"type\":\"user_input\",\"content\":[{\"type\":\"text\",\"text\":\"Hello Jane!\",\"annotations\":[{\"type\":\"speech_metadata\",\"speaker\":\"Joe\",\"style\":\"friendly\",\"future_field\":true}]}]}]")]
    public void BuildSpeechPayloadPreservesNativeInputAndWarnsWhenInstructionsAreIgnored(string inputProperty, string inputJson)
    {
        var nativeInput = JsonSerializer.Deserialize<JsonElement>(inputJson);
        var native = JsonSerializer.SerializeToElement(new Dictionary<string, object>
        {
            [inputProperty] = nativeInput,
            ["generation_config"] = new { temperature = 0.5 },
            ["future_option"] = new { enabled = true }
        });
        var originalNative = native.GetRawText();
        var request = new SpeechRequest
        {
            Model = "gemini-3.8-flash-tts",
            Text = "generic transcript",
            Instructions = "generic style",
            ProviderOptions = new() { ["google"] = native }
        };

        var (payload, warnings) = BuildPayload(request);

        Assert.True(JsonElement.DeepEquals(nativeInput, payload.GetProperty(inputProperty)));
        Assert.Equal(0.5, payload.GetProperty("generation_config").GetProperty("temperature").GetDouble());
        Assert.True(payload.GetProperty("future_option").GetProperty("enabled").GetBoolean());
        Assert.Equal(originalNative, request.ProviderOptions["google"].GetRawText());
        var warning = JsonSerializer.SerializeToElement(Assert.Single(warnings));
        Assert.Equal("ignored", warning.GetProperty("type").GetString());
        Assert.Equal("instructions", warning.GetProperty("feature").GetString());
        Assert.Equal("providerOptions.google.input takes precedence", warning.GetProperty("reason").GetString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    public void BuildSpeechPayloadDoesNotWarnForNativeInputAndBlankInstructions(string? instructions)
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.8-flash-tts",
            Text = "generic transcript",
            Instructions = instructions,
            ProviderOptions = new() { ["google"] = JsonSerializer.SerializeToElement(new { input = "native transcript" }) }
        });

        Assert.Equal("native transcript", payload.GetProperty("input").GetString());
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
    public void BuildSpeechPayloadMapsModelVoiceShortcutToBaseModelAndCanonicalVoice()
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview/Puck",
            Text = "Hello",
            Voice = "Kore"
        });

        Assert.Equal("gemini-3.1-flash-tts-preview", payload.GetProperty("model").GetString());

        var speechConfig = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single();
        Assert.Equal("Puck", speechConfig.GetProperty("voice").GetString());
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildSpeechPayloadMapsProviderQualifiedModelVoiceShortcut()
    {
        var (payload, warnings) = BuildPayload(new SpeechRequest
        {
            Model = "google/gemini-3.1-flash-tts-preview/Kore",
            Text = "Hello"
        });

        Assert.Equal("gemini-3.1-flash-tts-preview", payload.GetProperty("model").GetString());

        var speechConfig = payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single();
        Assert.Equal("Kore", speechConfig.GetProperty("voice").GetString());
        Assert.Empty(warnings);
    }

    [Fact]
    public void BuildSpeechPayloadRejectsUnknownModelShortcutVoice()
    {
        var ex = Assert.Throws<NotSupportedException>(() => BuildPayload(new SpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview/UnknownVoice",
            Text = "Hello"
        }));

        Assert.Contains("UnknownVoice", ex.Message);
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

    [Fact]
    public void BuildGoogleSpeechVoiceShortcutModelsAddsCanonicalVoiceModelsForTtsModelsOnly()
    {
        var method = typeof(GoogleAIProvider).GetMethod("BuildGoogleSpeechVoiceShortcutModels", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "BuildGoogleSpeechVoiceShortcutModels");

        var rawModels = new[]
        {
            new Model
            {
                Id = "google/gemini-3.1-flash-tts-preview",
                Name = "Gemini TTS",
                OwnedBy = "Google",
                Type = "speech",
                Created = 1910000000
            },
            new Model
            {
                Id = "google/gemini-3.5-flash",
                Name = "Gemini Flash",
                OwnedBy = "Google",
                Type = "language"
            }
        };

        var shortcuts = ((IEnumerable<Model>)method.Invoke(null, [rawModels, "google"])!).ToList();

        Assert.Equal(30, shortcuts.Count);
        Assert.Contains(shortcuts, model => model.Id == "google/gemini-3.1-flash-tts-preview/Kore" && model.Name == "gemini-3.1-flash-tts-preview/Kore" && model.Type == "speech");
        Assert.DoesNotContain(shortcuts, model => model.Id.Contains("gemini-3.5-flash", StringComparison.OrdinalIgnoreCase));
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
