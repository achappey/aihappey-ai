using System.Net;
using System.Text;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Cartesia;

public sealed class CartesiaTranscriptionCompatibilityTests
{
    [Fact]
    public async Task OpenAITranscriptionRequestAsyncWrapsBatchSttAndMapsVerboseResponse()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "type": "transcript",
                  "text": "hello world",
                  "language": "en",
                  "duration": 1.25,
                  "words": [
                    { "word": "hello", "start": 0, "end": 0.5 },
                    { "word": "world", "start": 0.5, "end": 1.25 }
                  ]
                }
                """);
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "transcription/ink-whisper",
            File = CreateAudioFile(Encoding.UTF8.GetBytes("audio")),
            ResponseFormat = "verbose_json",
            Language = "en",
            TimestampGranularities = ["word"]
        });

        var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
        Assert.Equal("hello world", verbose.Text);
        Assert.Equal("en", verbose.Language);
        Assert.Equal(1.25, verbose.Duration);
        Assert.Equal(2, verbose.Segments?.Length);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/stt", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);
        Assert.Equal("2025-04-16", capturedRequest.Headers.GetValues("Cartesia-Version").Single());

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=model", body);
        Assert.Contains("ink-whisper", body);
        Assert.Contains("name=language", body);
        Assert.Contains("timestamp_granularities", body);
        Assert.Contains("name=file", body);
    }

    [Fact]
    public async Task OpenAITranscriptionStreamingAsyncEmulatesDeltaThenDoneFromBatchStt()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            { "type": "transcript", "text": "hello world" }
            """));
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "ink-whisper",
                           File = CreateAudioFile(Encoding.UTF8.GetBytes("audio"))
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDelta>(first).Delta),
            second => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDone>(second).Text));
    }

    [Fact]
    public async Task VercelChatWithTranscriptionModelRoutesToBatchStt()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            { "type": "transcript", "text": "hello world" }
            """));

        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(new ChatRequest
                       {
                           Model = "cartesia/transcription/ink-whisper",
                           Messages =
                           [
                               new UIMessage
                               {
                                   Role = Role.user,
                                   Parts =
                                   [
                                       new FileUIPart
                                       {
                                           MediaType = "audio/wav",
                                           Url = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio"))
                                       }
                                   ]
                               }
                           ]
                       }))
        {
            parts.Add(part);
        }

        Assert.Contains(parts, part => part is TextDeltaUIMessageStreamPart { Delta: "hello world" });
    }

    [Fact]
    public async Task UnifiedConversationTranscriptionUsesBatchSttAndEmitsTextEvents()
    {
        var provider = CreateProvider(_ => JsonResponse("""
            { "type": "transcript", "text": "hello world" }
            """));
        var request = CreateUnifiedTranscriptionRequest();

        var response = await provider.ExecuteUnifiedAsync(request);
        var text = Assert.IsType<AITextContentPart>(Assert.Single(
            Assert.Single(response.Output!.Items!).Content!));
        Assert.Equal("hello world", text.Text);

        var events = new List<AIStreamEvent>();
        await foreach (var streamEvent in provider.StreamUnifiedAsync(request))
            events.Add(streamEvent);

        Assert.Equal(["text-start", "text-delta", "text-end", "finish"],
            events.Select(streamEvent => streamEvent.Event.Type));
        Assert.Equal("hello world", Assert.IsType<AITextDeltaEventData>(events[1].Event.Data).Delta);
    }

    private static AIRequest CreateUnifiedTranscriptionRequest()
        => new()
        {
            ProviderId = "cartesia",
            Model = "cartesia/transcription/ink-whisper",
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Role = "user",
                        Content =
                        [
                            new AIFileContentPart
                            {
                                Type = "file",
                                MediaType = "audio/wav",
                                Filename = "audio.wav",
                                Data = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio"))
                            }
                        ]
                    }
                ]
            }
        };

    private static IFormFile CreateAudioFile(byte[] audio)
        => new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.wav")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/wav"
        };

    private static CartesiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new FixedApiKeyResolver(), new FixedHttpClientFactory(new HttpClient(
            new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.cartesia.ai/")
        }));

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        foreach (var header in request.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (request.Content is not null)
        {
            clone.Content = new StringContent(
                request.Content.ReadAsStringAsync().GetAwaiter().GetResult(),
                Encoding.UTF8,
                request.Content.Headers.ContentType?.MediaType ?? "text/plain");
        }

        return clone;
    }

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
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
