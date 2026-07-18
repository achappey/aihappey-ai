using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Google;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIHappey.Tests.Google;

public sealed class GoogleOpenAITranscriptionCompatibilityTests
{
    [Fact]
    public async Task OpenAITranscriptionRequestAsyncUsesNativeGooglePayloadAndMapsVerboseResponse()
    {
        var audioBytes = Encoding.UTF8.GetBytes("audio-data");
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent(new
            {
                output_text = "Hello from Google"
            })
        });
        var provider = CreateProvider(handler);

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "google/gemini-3.5-flash",
            File = CreateAudioFile(audioBytes),
            ResponseFormat = "verbose_json",
            Language = "nl",
            Prompt = "Ignore this best-effort option"
        });

        var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
        Assert.Equal("Hello from Google", verbose.Text);
        Assert.Equal(string.Empty, verbose.Language);
        Assert.Equal(0, verbose.Duration);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1beta/interactions", request.RequestUri?.AbsolutePath);

        var payload = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("gemini-3.5-flash", payload.GetProperty("model").GetString());
        Assert.Equal("Generate a transcript of the speech. Do not include any other text.",
            payload.GetProperty("input").EnumerateArray().First().GetProperty("text").GetString());

        var audio = payload.GetProperty("input").EnumerateArray().Last();
        Assert.Equal("audio", audio.GetProperty("type").GetString());
        Assert.Equal(Convert.ToBase64String(audioBytes), audio.GetProperty("data").GetString());
        Assert.Equal("audio/wav", audio.GetProperty("mime_type").GetString());
    }

    [Fact]
    public async Task OpenAITranscriptionStreamingAsyncMapsTextDeltasAndEmitsFinalDoneEvent()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(
                new
                {
                    event_type = "step.delta",
                    delta = new { type = "text", text = "Hello " }
                },
                new
                {
                    event_type = "step.delta",
                    delta = new { type = "text", text = "world" }
                },
                new
                {
                    event_type = "interaction.completed",
                    output_text = "Hello world"
                })
        });
        var provider = CreateProvider(handler);
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "gemini-3.5-flash",
                           File = CreateAudioFile(Encoding.UTF8.GetBytes("audio-data"))
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal("Hello ", Assert.IsType<OpenAITranscriptionTextDelta>(first).Delta),
            second => Assert.Equal("world", Assert.IsType<OpenAITranscriptionTextDelta>(second).Delta),
            third => Assert.Equal("Hello world", Assert.IsType<OpenAITranscriptionTextDone>(third).Text));

        var request = Assert.Single(handler.Requests);
        var payload = JsonDocument.Parse(request.Body!).RootElement;
        Assert.True(payload.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task OpenAITranscriptionStreamingAsyncUsesCompletedTranscriptWhenNoDeltaWasReceived()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = SseContent(new
            {
                event_type = "interaction.completed",
                output_text = "Complete transcript"
            })
        });
        var provider = CreateProvider(handler);
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "gemini-3.5-flash",
                           File = CreateAudioFile(Encoding.UTF8.GetBytes("audio-data"))
                       }))
        {
            events.Add(streamEvent);
        }

        var done = Assert.IsType<OpenAITranscriptionTextDone>(Assert.Single(events));
        Assert.Equal("Complete transcript", done.Text);
    }

    private static IFormFile CreateAudioFile(byte[] audio)
        => new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

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
            .Select(@event => $"data: {JsonSerializer.Serialize(@event, JsonSerializerOptions.Web)}")
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

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));

            Assert.True(responses.TryDequeue(out var response),
                $"No response queued for {request.Method} {request.RequestUri}.");
            return response;
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri? RequestUri, string? Body);
}
