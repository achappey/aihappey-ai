using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ElevenLabs;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.ElevenLabs;

public sealed class ElevenLabsProviderOpenAISpeechTests
{
    [Fact]
    public async Task Buffered_tts_maps_openai_fields_and_flat_extensions()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = Clone(request);
            return AudioResponse("tts-audio", "audio/mpeg");
        });

        var (audio, mime) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "elevenlabs/eleven_multilingual_v2",
            Input = "Hello ElevenLabs",
            Voice = "voice/id",
            ResponseFormat = "mp3",
            Speed = 1.2f,
            AdditionalProperties = Extensions(new
            {
                enable_logging = false,
                language_code = "en",
                seed = 42,
                voice_settings = new { stability = 0.7 }
            })
        });

        Assert.Equal(Encoding.UTF8.GetBytes("tts-audio"), audio);
        Assert.Equal("audio/mpeg", mime);
        Assert.Equal("/v1/text-to-speech/voice%2Fid", captured!.RequestUri!.AbsolutePath);
        Assert.Contains("output_format=mp3_44100_128", captured.RequestUri.Query);
        Assert.Contains("enable_logging=false", captured.RequestUri.Query);
        using var json = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        Assert.Equal("eleven_multilingual_v2", json.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("en", json.RootElement.GetProperty("language_code").GetString());
        Assert.Equal(42, json.RootElement.GetProperty("seed").GetInt32());
        Assert.Equal(0.7, json.RootElement.GetProperty("voice_settings").GetProperty("stability").GetDouble());
        Assert.Equal(1.2f, json.RootElement.GetProperty("voice_settings").GetProperty("speed").GetSingle());
    }

    [Fact]
    public async Task Buffered_timestamp_tts_decodes_audio_base64()
    {
        var expected = Encoding.UTF8.GetBytes("timestamp-audio");
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = Clone(request);
            return JsonResponse(new { audio_base64 = Convert.ToBase64String(expected), alignment = new { } });
        });

        var (audio, mime) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "eleven_multilingual_v2",
            Input = "Timed",
            Voice = "voice",
            ResponseFormat = "opus",
            AdditionalProperties = Extensions(new { with_timestamps = true })
        });

        Assert.Equal(expected, audio);
        Assert.Equal("audio/ogg", mime);
        Assert.Equal("/v1/text-to-speech/voice/with-timestamps", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Native_binary_tts_stream_emits_audio_then_done()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = Clone(request);
            return AudioResponse("stream-audio", "audio/mpeg");
        });

        var events = await ReadEvents(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "eleven_turbo_v2_5", Input = "Stream", Voice = "voice"
        }));

        var delta = Assert.IsType<AudioSpeechStreamDelta>(events[0]);
        Assert.Equal("stream-audio", Encoding.UTF8.GetString(Convert.FromBase64String(delta.Audio)));
        Assert.IsType<AudioSpeechStreamDone>(events[^1]);
        Assert.Equal("/v1/text-to-speech/voice/stream", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Timestamp_dialogue_stream_extracts_audio_and_discards_timing_metadata()
    {
        HttpRequestMessage? captured = null;
        var first = Convert.ToBase64String(Encoding.UTF8.GetBytes("one"));
        var second = Convert.ToBase64String(Encoding.UTF8.GetBytes("two"));
        var provider = CreateProvider(request =>
        {
            captured = Clone(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"{{\"audio_base64\":\"{first}\",\"alignment\":{{}}}}\n{{\"audio_base64\":\"{second}\",\"voice_segments\":[]}}\n", Encoding.UTF8, "application/json")
            };
        });

        var events = await ReadEvents(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "eleven_v3/text-to-dialogue",
            Input = "Dialogue is supplied by inputs",
            AdditionalProperties = Extensions(new
            {
                with_timestamps = true,
                inputs = new[]
                {
                    new { text = "Hello", voice_id = "a" },
                    new { text = "Hi", voice_id = "b" }
                }
            })
        }));

        Assert.Equal(3, events.Count);
        Assert.Equal(first, Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.Equal(second, Assert.IsType<AudioSpeechStreamDelta>(events[1]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[2]);
        Assert.Equal("/v1/text-to-dialogue/stream/with-timestamps", captured!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task Detailed_music_sse_extracts_nested_audio()
    {
        HttpRequestMessage? captured = null;
        var audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("music"));
        var provider = CreateProvider(request =>
        {
            captured = Clone(request);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent($"event: audio\ndata: {{\"audio\":\"{audio}\",\"metadata\":{{\"ignored\":true}}}}\n\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var events = await ReadEvents(provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
        {
            Model = "music_v2",
            Input = "A concise instrumental theme",
            ResponseFormat = "auto",
            AdditionalProperties = Extensions(new { detailed_stream = true, with_waveform_visual = true })
        }));

        Assert.Equal(audio, Assert.IsType<AudioSpeechStreamDelta>(events[0]).Audio);
        Assert.IsType<AudioSpeechStreamDone>(events[1]);
        Assert.Equal("/v1/music/detailed/stream", captured!.RequestUri!.AbsolutePath);
        using var body = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        Assert.Equal("music_v2", body.RootElement.GetProperty("model_id").GetString());
        Assert.Equal("A concise instrumental theme", body.RootElement.GetProperty("prompt").GetString());
    }

    private static async Task<List<IAudioSpeechStreamEvent>> ReadEvents(IAsyncEnumerable<IAudioSpeechStreamEvent> source)
    {
        var result = new List<IAudioSpeechStreamEvent>();
        await foreach (var item in source) result.Add(item);
        return result;
    }

    private static Dictionary<string, JsonElement> Extensions(object value)
        => JsonSerializer.SerializeToElement(value).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    private static HttpResponseMessage AudioResponse(string value, string mediaType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(value)) };
        response.Content.Headers.ContentType = new(mediaType);
        return response;
    }

    private static HttpResponseMessage JsonResponse(object value)
        => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private static ElevenLabsProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new ApiKeyResolver(), new HttpClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content is not null)
            clone.Content = new StringContent(request.Content.ReadAsStringAsync().GetAwaiter().GetResult(), Encoding.UTF8, "application/json");
        return clone;
    }

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
