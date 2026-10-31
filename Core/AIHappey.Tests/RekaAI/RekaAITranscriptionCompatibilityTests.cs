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

        var transcriptionRequest = new TranscriptionRequest
        {
            Model = "rekaai/reka-flash-3-stt",
            Audio = Convert.ToBase64String(audio),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["rekaai"] = JsonSerializer.SerializeToElement(new
                {
                    model = "metadata-model",
                    prompt = "Preserve punctuation.",
                    temperature = 0.2,
                    language = "nl",
                    sampling_rate = 16000,
                    future_object = new { nested = new object[] { new { value = 1 }, new object[] { true, null!, "text" } } },
                    future_array = new object[] { 1, new { more = new object[] { false, 2.5 } }, Array.Empty<int>() },
                    future_string = "unchanged",
                    future_number = 12345678901234567890.123456789m,
                    future_boolean = false,
                    future_null = (string?)null
                }),
                ["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not-forwarded" })
            }
        };
        var response = await provider.TranscriptionRequest(transcriptionRequest);

        Assert.Equal("Hello from Reka", response.Text);
        Assert.Empty(response.Segments!);

        var providerOptions = transcriptionRequest.ProviderOptions!;
        var recordedRequest = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, recordedRequest.Method);
        Assert.Equal("/v1/chat/completions", recordedRequest.Uri?.AbsolutePath);

        var payload = JsonDocument.Parse(recordedRequest.Body!).RootElement;
        Assert.Equal("reka-flash-3-stt", payload.GetProperty("model").GetString());
        Assert.Equal(0.2, payload.GetProperty("temperature").GetDouble());
        Assert.Contains("rekaai", providerOptions.Keys);
        Assert.Contains("another-provider", providerOptions.Keys);
        Assert.True(providerOptions["rekaai"].TryGetProperty("future_object", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("rekaai", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(payload.TryGetProperty("language", out _));
        Assert.False(payload.TryGetProperty("sampling_rate", out _));

        using var providerOptionsDocument = JsonDocument.Parse("""
            {
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        foreach (var property in providerOptionsDocument.RootElement.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);

        var content = payload.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("audio_url", content[0].GetProperty("type").GetString());
        Assert.Equal($"data:audio/wav;base64,{Convert.ToBase64String(audio)}", content[0].GetProperty("audio_url").GetString());
        Assert.Contains("Preserve punctuation.", content[1].GetProperty("text").GetString());

        var warnings = JsonSerializer.Serialize(response.Warnings, JsonSerializerOptions.Web);
        Assert.Contains("language", warnings);
        Assert.Contains("sampling_rate", warnings);
    }

    [Fact]
    public async Task TranscriptionRequestPreservesProviderMetadataValidation()
    {
        var handler = new RecordingHandler(JsonResponse(new
        {
            choices = new[] { new { message = new { role = "assistant", content = "unused" } } }
        }));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<JsonException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "rekaai/reka-flash-3-stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio-data")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["rekaai"] = JsonSerializer.SerializeToElement(new { temperature = "not-a-number" })
            }
        }));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task TranscriptionRequestPreservesUpstreamErrorBehavior()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request", Encoding.UTF8, "application/json")
        });
        var provider = CreateProvider(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "rekaai/reka-flash-3-stt",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio-data")),
            MediaType = "audio/wav"
        }));

        Assert.Equal("RekaAI transcription failed (400): invalid request", exception.Message);
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
        Assert.False(payload.TryGetProperty("temperature", out _));
        Assert.Contains("Transcribe the supplied audio faithfully.",
            payload.GetProperty("messages")[0].GetProperty("content")[1].GetProperty("text").GetString());
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
