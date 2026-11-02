using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Vidu;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vidu;

public sealed class ViduProviderSpeechMetadataPassthroughTests
{
    [Fact]
    public async Task Text_to_speech_forwards_unknown_options_and_preserves_known_overrides()
    {
        string? requestBody = null;
        var provider = CreateProvider(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/ent/v2/audio-tts")
            {
                requestBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""{"state":"success","file_url":"https://api.vidu.com/audio.mp3"}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/audio.mp3")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        using var metadataDocument = JsonDocument.Parse(
            """
            {
              "voice_setting_voice_id": "metadata-voice",
              "voice_setting_speed": 1.5,
              "voice_setting_volume": 7,
              "voice_setting_pitch": -2,
              "voice_setting_emotion": "happy",
              "future_config": {"levels": [1, null, {"enabled": true}]},
              "future_array": [true, "kept", 2.5],
              "future_primitive": 23,
              "future_null": null
            }
            """);

        var request = new SpeechRequest
        {
            Model = "audio-tts",
            Text = "Hello",
            Voice = "request-voice",
            Speed = 1.25f,
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vidu"] = metadataDocument.RootElement.Clone()
            }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        Assert.True(JsonElement.DeepEquals(
            metadataDocument.RootElement,
            clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("vidu")));

        await provider.SpeechRequest(request);

        using var payloadDocument = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var payload = payloadDocument.RootElement;
        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice_setting_voice_id").GetString());
        Assert.Equal(1.25, payload.GetProperty("voice_setting_speed").GetDouble());
        Assert.Equal(7, payload.GetProperty("voice_setting_volume").GetInt32());
        Assert.Equal(-2, payload.GetProperty("voice_setting_pitch").GetInt32());
        Assert.Equal("happy", payload.GetProperty("voice_setting_emotion").GetString());
        Assert.False(payload.TryGetProperty("duration", out _));
        Assert.False(payload.TryGetProperty("callback_url", out _));
        Assert.Equal(3, payload.GetProperty("future_config").GetProperty("levels").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_config").GetProperty("levels")[1].ValueKind);
        Assert.True(payload.GetProperty("future_config").GetProperty("levels")[2].GetProperty("enabled").GetBoolean());
        Assert.True(payload.GetProperty("future_array")[0].GetBoolean());
        Assert.Equal("kept", payload.GetProperty("future_array")[1].GetString());
        Assert.Equal(23, payload.GetProperty("future_primitive").GetInt32());
        Assert.False(payload.TryGetProperty("future_null", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("vidu", out _));
    }

    [Fact]
    public async Task Text_to_audio_forwards_unknown_options_and_preserves_known_parameters()
    {
        string? requestBody = null;
        var provider = CreateProvider(async request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri?.AbsolutePath == "/ent/v2/text2audio")
            {
                requestBody = await request.Content!.ReadAsStringAsync();
                return JsonResponse("""{"task_id":"task-1"}""");
            }

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/ent/v2/tasks/task-1/creations")
                return JsonResponse("""{"state":"success","creations":[{"url":"https://api.vidu.com/audio.mp3"}]}""");

            if (request.Method == HttpMethod.Get && request.RequestUri?.AbsolutePath == "/audio.mp3")
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                };

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });

        using var metadataDocument = JsonDocument.Parse(
            """
            {
              "duration": 4,
              "seed": 17,
              "callback_url": "https://example.test/complete",
              "future_object": {"items": [false, null, {"value": "kept"}]},
              "future_enabled": true
            }
            """);

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "audio1.0",
            Text = "A quiet river",
            ProviderOptions = new Dictionary<string, JsonElement> { ["vidu"] = metadataDocument.RootElement.Clone() }
        });

        using var payloadDocument = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var payload = payloadDocument.RootElement;
        Assert.Equal("audio1.0", payload.GetProperty("model").GetString());
        Assert.Equal("A quiet river", payload.GetProperty("prompt").GetString());
        Assert.Equal(4, payload.GetProperty("duration").GetInt32());
        Assert.Equal(17, payload.GetProperty("seed").GetInt32());
        Assert.Equal("https://example.test/complete", payload.GetProperty("callback_url").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_object").GetProperty("items")[1].ValueKind);
        Assert.Equal("kept", payload.GetProperty("future_object").GetProperty("items")[2].GetProperty("value").GetString());
        Assert.True(payload.GetProperty("future_enabled").GetBoolean());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("vidu", out _));
    }

    [Fact]
    public async Task Text_to_speech_preserves_known_option_validation()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "audio-tts",
            Text = "Hello",
            Voice = "voice",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vidu"] = JsonSerializer.SerializeToElement(new { voice_setting_volume = "loud" })
            }
        }));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "audio-tts",
            Text = "Hello",
            Voice = "voice",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vidu"] = JsonSerializer.SerializeToElement(new { voice_setting_speed = 2.5 })
            }
        }));
        Assert.Equal("VoiceSettingSpeed", error.ParamName);
    }

    [Fact]
    public async Task Text_to_audio_preserves_duration_validation()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        var error = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "audio1.0",
            Text = "A quiet river",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["vidu"] = JsonSerializer.SerializeToElement(new { duration = 1 })
            }
        }));

        Assert.Equal("Duration", error.ParamName);
    }

    [Fact]
    public async Task Text_to_speech_preserves_upstream_error_details()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        }));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.SpeechRequest(new SpeechRequest
            {
                Model = "audio-tts",
                Text = "Hello",
                Voice = "voice"
            }));

        Assert.Equal("Vidu text-to-speech request failed (400): upstream failure", error.Message);
    }

    private static ViduProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new DelegateHttpMessageHandler(responder));
        return new ViduProvider(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "vidu" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
