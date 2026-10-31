using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Vogent;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Vogent;

public sealed class VogentProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Speech_preserves_keyed_metadata_and_forwards_unknown_json_at_the_upstream_root()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var options = JsonDocument.Parse(
            """
            {
              "voiceId": "metadata-voice",
              "outputType": "MP3",
              "sampleRate": 48000,
              "voiceOptionValues": [
                {
                  "optionId": "stability",
                  "value": "0.5",
                  "future_option": {"values": [true, null, {"name": "native"}]}
                }
              ],
              "future_object": {
                "values": [1, null, {"enabled": true}],
                "explicit_null": null
              },
              "future_array": ["text", 2, null],
              "future_number": 3,
              "future_boolean": false
            }
            """);
        using (options)
        {
            var request = new SpeechRequest
            {
                Model = "tts/model-voice",
                Text = "Hello from Vogent.",
                ProviderOptions = new Dictionary<string, JsonElement>
                {
                    ["vogent"] = options.RootElement.Clone()
                }
            };

            using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
            var clientOptions = clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("vogent");
            Assert.Equal(JsonValueKind.Array, clientOptions.GetProperty("future_array").ValueKind);
            Assert.Equal(JsonValueKind.Null, clientOptions.GetProperty("future_object").GetProperty("explicit_null").ValueKind);

            var response = await provider.SpeechRequest(request);

            Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
            using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
            var root = payload.RootElement;
            Assert.Equal("Hello from Vogent.", root.GetProperty("text").GetString());
            Assert.Equal("model-voice", root.GetProperty("voiceId").GetString());
            Assert.Equal("MP3", root.GetProperty("outputType").GetString());
            Assert.Equal(48000, root.GetProperty("sampleRate").GetInt32());
            Assert.Equal("MP3", root.GetProperty("format").GetProperty("outputType").GetString());
            Assert.Equal(48000, root.GetProperty("format").GetProperty("sampleRate").GetInt32());
            var voiceOption = root.GetProperty("voiceOptionValues")[0];
            Assert.Equal("stability", voiceOption.GetProperty("optionId").GetString());
            Assert.Equal("0.5", voiceOption.GetProperty("value").GetString());
            Assert.True(voiceOption.GetProperty("future_option").GetProperty("values")[0].GetBoolean());
            Assert.Equal(JsonValueKind.Null, voiceOption.GetProperty("future_option").GetProperty("values")[1].ValueKind);
            Assert.Equal("native", voiceOption.GetProperty("future_option").GetProperty("values")[2].GetProperty("name").GetString());
            Assert.Equal(1, root.GetProperty("future_object").GetProperty("values")[0].GetInt32());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("future_object").GetProperty("values")[1].ValueKind);
            Assert.True(root.GetProperty("future_object").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, root.GetProperty("future_object").GetProperty("explicit_null").ValueKind);
            Assert.Equal(JsonValueKind.Array, root.GetProperty("future_array").ValueKind);
            Assert.Equal(3, root.GetProperty("future_number").GetInt32());
            Assert.False(root.GetProperty("future_boolean").GetBoolean());
            Assert.False(root.TryGetProperty("providerOptions", out _));
            Assert.False(root.TryGetProperty("vogent", out _));
        }
    }

    [Fact]
    public async Task Speech_keeps_default_omission_and_metadata_precedence()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });
        var request = new SpeechRequest
        {
            Model = "tts/model-voice",
            Text = "Hello.",
            OutputFormat = "wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vogent"] = JsonSerializer.SerializeToElement(new
                {
                    outputType = "MP3",
                    sampleRate = 32000,
                    voiceOptionValues = Array.Empty<object>(),
                    future_flag = true
                })
            }
        };

        await provider.SpeechRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("MP3", root.GetProperty("outputType").GetString());
        Assert.Equal("WAV_PCM16", root.GetProperty("format").GetProperty("outputType").GetString());
        Assert.Equal(32000, root.GetProperty("format").GetProperty("sampleRate").GetInt32());
        Assert.Empty(root.GetProperty("voiceOptionValues").EnumerateArray());
        Assert.True(root.GetProperty("future_flag").GetBoolean());

        requestBody = null;
        var defaultsProvider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });

        await defaultsProvider.SpeechRequest(new SpeechRequest
        {
            Model = "tts/model-voice",
            Text = "Hello."
        });

        using var defaultsPayload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        Assert.False(defaultsPayload.RootElement.TryGetProperty("format", out _));
        Assert.False(defaultsPayload.RootElement.TryGetProperty("voiceOptionValues", out _));
    }

    [Fact]
    public async Task Multispeaker_speech_preserves_nested_unknown_fields_and_normalizes_known_fields()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1])
            };
        });
        using var lines = JsonDocument.Parse(
            """
            {
              "lines": [
                {
                  "text": "  Hello.  ",
                  "voiceId": " voice-1 ",
                  "future_line": {"values": [1, null]}
                }
              ],
              "future_root": {"enabled": true}
            }
            """);
        var request = new SpeechRequest
        {
            Model = "tts/multispeaker",
            Text = "Ignored for multispeaker mode.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vogent"] = lines.RootElement.Clone()
            }
        };

        await provider.SpeechRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        var line = root.GetProperty("lines")[0];
        Assert.Equal("Hello.", line.GetProperty("text").GetString());
        Assert.Equal("voice-1", line.GetProperty("voiceId").GetString());
        Assert.Equal(1, line.GetProperty("future_line").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, line.GetProperty("future_line").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("future_root").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Multispeaker_speech_preserves_missing_required_line_validation()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "tts/multispeaker",
            Text = "Hello.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vogent"] = JsonSerializer.SerializeToElement(new
                {
                    lines = new[] { new { text = "missing voice" } }
                })
            }
        };

        var error = await Assert.ThrowsAsync<ArgumentException>(() => provider.SpeechRequest(request));
        Assert.Contains("Every Vogent multispeaker line requires non-empty text and voiceId values.", error.Message);
    }

    [Fact]
    public async Task Speech_preserves_invalid_known_metadata_type_validation()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "tts/voice-1",
            Text = "Hello.",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vogent"] = JsonSerializer.SerializeToElement(new { sampleRate = "not-a-number" })
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(request));
    }

    [Fact]
    public async Task Speech_preserves_upstream_error_details()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SpeechRequest(new SpeechRequest
            {
                Model = "tts/voice-1",
                Text = "Hello."
            }));

        Assert.Equal("Vogent TTS failed (400): upstream failure", error.Message);
    }

    private static VogentProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var client = new HttpClient(new CapturingHandler(responder));
        return new VogentProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));
    }

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
