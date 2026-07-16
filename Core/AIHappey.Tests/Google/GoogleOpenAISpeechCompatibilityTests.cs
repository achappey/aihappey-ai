using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Google;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleOpenAISpeechCompatibilityTests
{
    [Fact]
    public async Task OpenAISpeechRequestAsyncWrapsNativeGoogleSpeechResponseAsBinaryAudio()
    {
        var audioBytes = Encoding.UTF8.GetBytes("pcm-audio");
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                output_audio = new
                {
                    data = Convert.ToBase64String(audioBytes),
                    mime_type = "audio/L16;rate=24000"
                }
            })
        });
        var provider = CreateProvider(handler);

        var (audio, mimeType) = await provider.OpenAISpeechRequestAsync(new AudioSpeechRequest
        {
            Model = "gemini-3.1-flash-tts-preview/Puck",
            Input = "Say cheerfully: Have a wonderful day!",
            Voice = "Kore"
        });

        Assert.Equal(audioBytes, audio);
        Assert.Equal("audio/L16;rate=24000", mimeType);
        Assert.Single(handler.Requests);

        var payload = JsonDocument.Parse(handler.Requests[0].Body!).RootElement;
        Assert.Equal("gemini-3.1-flash-tts-preview", payload.GetProperty("model").GetString());
        Assert.Equal("Say cheerfully: Have a wonderful day!", payload.GetProperty("input").GetString());
        Assert.Equal("audio", payload.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("Puck", payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single().GetProperty("voice").GetString());
    }

    [Fact]
    public async Task OpenAISpeechStreamingAsyncPostsNativeGoogleStreamPayloadAndMapsAudioDeltas()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(
                new
                {
                    event_type = "step.delta",
                    delta = new
                    {
                        type = "audio",
                        data = "audio-chunk-1",
                        mime_type = "audio/L16"
                    }
                },
                new
                {
                    event_type = "interaction.completed",
                    usage = new
                    {
                        input_tokens = 11,
                        output_tokens = 22,
                        total_tokens = 33
                    }
                })
        });
        var provider = CreateProvider(handler);

        var events = new List<IAudioSpeechStreamEvent>();
        await foreach (var streamEvent in provider.OpenAISpeechStreamingAsync(new AudioSpeechRequest
                       {
                           Model = "gemini-3.1-flash-tts-preview",
                           Input = "Say cheerfully: Have a wonderful day!",
                           Voice = "Kore"
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first =>
            {
                var delta = Assert.IsType<AudioSpeechStreamDelta>(first);
                Assert.Equal("speech.audio.delta", delta.Type);
                Assert.Equal("audio-chunk-1", delta.Audio);
            },
            second =>
            {
                var done = Assert.IsType<AudioSpeechStreamDone>(second);
                Assert.Equal("speech.audio.done", done.Type);
                Assert.Equal(11, done.Usage?.InputTokens);
                Assert.Equal(22, done.Usage?.OutputTokens);
                Assert.Equal(33, done.Usage?.TotalTokens);
            });

        Assert.Single(handler.Requests);
        var request = handler.Requests[0];
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1beta/interactions", request.RequestUri?.AbsolutePath);

        var payload = JsonDocument.Parse(request.Body!).RootElement;
        Assert.True(payload.GetProperty("stream").GetBoolean());
        Assert.Equal("gemini-3.1-flash-tts-preview", payload.GetProperty("model").GetString());
        Assert.Equal("audio", payload.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("Kore", payload.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single().GetProperty("voice").GetString());
    }

    [Fact]
    public void BuildOpenAISpeechStreamingPayloadUsesExistingSpeechPayloadAndForcesStreamTrue()
    {
        var warnings = new List<object>();
        var method = typeof(GoogleAIProvider).GetMethod("BuildOpenAISpeechStreamingPayload", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(GoogleAIProvider), "BuildOpenAISpeechStreamingPayload");

        var payload = (JsonObject)method.Invoke(null,
        [
            new AudioSpeechRequest
            {
                Model = "google/gemini-3.1-flash-tts-preview/Charon",
                Input = "Hello",
                Voice = "Kore"
            },
            warnings
        ])!;

        var json = JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web);
        Assert.True(json.GetProperty("stream").GetBoolean());
        Assert.Equal("gemini-3.1-flash-tts-preview", json.GetProperty("model").GetString());
        Assert.Equal("Hello", json.GetProperty("input").GetString());
        Assert.Equal("Charon", json.GetProperty("generation_config").GetProperty("speech_config").EnumerateArray().Single().GetProperty("voice").GetString());
        Assert.Contains(warnings, warning => warning.ToString()!.Contains("voice", StringComparison.OrdinalIgnoreCase));
    }

    private static GoogleAIProvider CreateProvider(RecordingHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://generativelanguage.googleapis.com/")
        };

        return new GoogleAIProvider(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            NullLogger<GoogleAIProvider>.Instance,
            new FixedHttpClientFactory(httpClient));
    }

    private static StringContent JsonContent(object payload)
        => new(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json");

    private static StringContent SseContent(params object[] events)
    {
        var lines = events
            .Select(e => $"data: {JsonSerializer.Serialize(e, JsonSerializerOptions.Web)}")
            .Append("data: [DONE]");

        return new StringContent(string.Join("\n\n", lines), Encoding.UTF8, "text/event-stream");
    }

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] queuedResponses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(queuedResponses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            Assert.True(responses.TryDequeue(out var response), $"No response queued for {request.Method} {request.RequestUri}.");
            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
