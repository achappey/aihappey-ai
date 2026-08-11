using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Zai;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Zai;

public sealed class ZaiProviderOpenAICompatibilityTests
{
    [Fact]
    public async Task Image_generation_uses_Zai_endpoint_and_maps_response()
    {
        AuthenticationHeaderValue? authorization = null;
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/paas/v4/images/generations", request.RequestUri?.AbsolutePath);
            authorization = request.Headers.Authorization;
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new
            {
                created = 123L,
                data = new[] { new { url = "https://images.example/generated.png" } }
            });
        });

        var response = await provider.OpenAIImageGenerationRequestAsync(new()
        {
            Model = "glm-image",
            Prompt = "A kitten",
            Quality = "hd",
            Size = "1280x1280"
        });

        Assert.Equal("Bearer", authorization?.Scheme);
        Assert.Equal("test-api-key", authorization?.Parameter);
        Assert.Contains("\"model\":\"glm-image\"", requestJson);
        Assert.Contains("\"prompt\":\"A kitten\"", requestJson);
        Assert.Equal(123L, response.Created);
#pragma warning disable CS0618
        Assert.Equal("https://images.example/generated.png", Assert.Single(response.Data!).Url);
#pragma warning restore CS0618
    }

    [Fact]
    public async Task Synchronous_transcription_forwards_only_supported_shared_fields()
    {
        AuthenticationHeaderValue? authorization = null;
        string? multipart = null;
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/api/paas/v4/audio/transcriptions", request.RequestUri?.AbsolutePath);
            authorization = request.Headers.Authorization;
            multipart = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse(new { id = "task-1", model = "glm-asr-2512", text = "hello world" });
        });

        var response = await provider.OpenAITranscriptionRequestAsync(CreateTranscriptionRequest(stream: false));

        Assert.Equal("test-api-key", authorization?.Parameter);
        Assert.Contains("name=file", multipart);
        Assert.Contains("sample.wav", multipart);
        Assert.Contains("name=model", multipart);
        Assert.Contains("glm-asr-2512", multipart);
        Assert.Contains("name=prompt", multipart);
        Assert.Contains("Prior context", multipart);
        Assert.Contains("name=stream", multipart);
        Assert.Contains("false", multipart);
        Assert.Equal("hello world", response.Text);
    }

    [Fact]
    public async Task Streaming_transcription_yields_native_delta_and_done_events()
    {
        string? multipart = null;
        var provider = CreateProvider(request =>
        {
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "text/event-stream");
            multipart = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            const string sse = "data: {\"id\":\"task-1\",\"type\":\"transcript.text.delta\",\"delta\":\"hello \"}\n\n"
                             + "data: {\"id\":\"task-1\",\"type\":\"transcript.text.delta\",\"delta\":\"world\"}\n\n"
                             + "data: {\"id\":\"task-1\",\"type\":\"transcript.text.done\",\"delta\":\"hello world\"}\n\n"
                             + "data: [DONE]\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
            };
        });

        var events = new List<IOpenAITranscriptionStreamEvent>();
        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(CreateTranscriptionRequest(stream: true)))
            events.Add(streamEvent);

        Assert.Contains("true", multipart);
        Assert.Collection(events,
            item => Assert.Equal("hello ", Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item => Assert.Equal("world", Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item => Assert.Equal("hello world", Assert.IsType<OpenAITranscriptionTextDone>(item).Text));
    }

    [Fact]
    public async Task Transcription_backend_error_includes_status_and_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"code\":1001,\"message\":\"invalid audio\"}", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.OpenAITranscriptionRequestAsync(CreateTranscriptionRequest(stream: false)));

        Assert.Contains("400", exception.Message);
        Assert.Contains("invalid audio", exception.Message);
    }

    private static OpenAITranscriptionRequest CreateTranscriptionRequest(bool stream)
    {
        byte[] audio = [1, 2, 3, 4];
        return new OpenAITranscriptionRequest
        {
            File = new FormFile(new MemoryStream(audio), 0, audio.Length, "file", "sample.wav")
            {
                Headers = new HeaderDictionary(),
                ContentType = "audio/wav"
            },
            Model = "glm-asr-2512",
            Prompt = "Prior context",
            Stream = stream
        };
    }

    private static ZaiProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var apiClient = new HttpClient(new DelegateHttpMessageHandler(responder));
        var downloadClient = new HttpClient(new DelegateHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)));
        return new ZaiProvider(new StaticApiKeyResolver(), new SequencedHttpClientFactory(apiClient, downloadClient));
    }

    private static HttpResponseMessage JsonResponse(object payload)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SequencedHttpClientFactory(params HttpClient[] clients) : IHttpClientFactory
    {
        private int _index;

        public HttpClient CreateClient(string name) => clients[_index++];
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
