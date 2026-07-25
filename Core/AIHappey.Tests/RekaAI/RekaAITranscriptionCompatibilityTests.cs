using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.RekaAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RekaAI;

public sealed class RekaAITranscriptionCompatibilityTests
{
    [Fact]
    public async Task TranscriptionRequestUsesChatCompletionsWithBase64AudioAndMapsText()
    {
        var handler = new RecordingHandler(JsonResponse(new
        {
            id = "chatcmpl-reka-transcription",
            model = "reka-flash-3-stt",
            choices = new[] { new { message = new { role = "assistant", content = "Hello from Reka" } } },
            usage = new { input_tokens = 12, output_tokens = 4 }
        }));
        var provider = CreateProvider(handler);
        var audio = Encoding.UTF8.GetBytes("audio-data");

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "rekaai/reka-flash-3-stt",
            Audio = Convert.ToBase64String(audio),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["rekaai"] = JsonSerializer.SerializeToElement(new
                {
                    prompt = "Preserve punctuation.",
                    temperature = 0.2,
                    language = "nl",
                    sampling_rate = 16000
                })
            }
        });

        Assert.Equal("Hello from Reka", response.Text);
        Assert.Empty(response.Segments!);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("/v1/chat/completions", request.Uri?.AbsolutePath);

        var payload = JsonDocument.Parse(request.Body!).RootElement;
        Assert.Equal("reka-flash-3-stt", payload.GetProperty("model").GetString());
        Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());

        var content = payload.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("audio_url", content[0].GetProperty("type").GetString());
        Assert.Equal($"data:audio/wav;base64,{Convert.ToBase64String(audio)}", content[0].GetProperty("audio_url").GetString());
        Assert.Contains("Preserve punctuation.", content[1].GetProperty("text").GetString());

        var warnings = JsonSerializer.Serialize(response.Warnings, JsonSerializerOptions.Web);
        Assert.Contains("language", warnings);
        Assert.Contains("sampling_rate", warnings);
    }

    [Fact]
    public async Task OpenAITranscriptionRequestAsyncUsesChatResponseConversion()
    {
        var handler = new RecordingHandler(JsonResponse(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "OpenAI compatibility transcript" } } }
        }));
        var provider = CreateProvider(handler);

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "rekaai/reka-flash-3-stt",
            File = CreateAudioFile(Encoding.UTF8.GetBytes("audio-data")),
            Prompt = "Keep the speaker's words unchanged.",
            Temperature = (float)0.1
        });

        Assert.Equal("OpenAI compatibility transcript", response.Text);

        var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!).RootElement;
        Assert.Equal(0.1, payload.GetProperty("temperature").GetDouble());
        Assert.Contains("Keep the speaker's words unchanged.",
            payload.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString());
    }

    [Fact]
    public async Task OpenAITranscriptionStreamingAsyncMapsChatDeltasAndEmitsDone()
    {
        var handler = new RecordingHandler(SseResponse(
            new { choices = new[] { new { delta = new { content = "Hello " } } } },
            new { choices = new[] { new { delta = new { content = "world" }, finish_reason = "stop" } } }));
        var provider = CreateProvider(handler);
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "reka-flash-3-stt",
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

        var payload = JsonDocument.Parse(Assert.Single(handler.Requests).Body!).RootElement;
        Assert.True(payload.GetProperty("stream").GetBoolean());
    }

    private static IFormFile CreateAudioFile(byte[] audio)
        => new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

    private static RekaAIProvider CreateProvider(RecordingHandler handler)
        => new(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new FixedHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.reka.ai/") }));

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(params object[] events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Join("\n\n", events.Select(@event => $"data: {JsonSerializer.Serialize(@event, JsonSerializerOptions.Web)}").Append("data: [DONE]")),
                Encoding.UTF8,
                "text/event-stream")
        };

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

    private sealed record RecordedRequest(HttpMethod Method, Uri? Uri, string? Body);
}
