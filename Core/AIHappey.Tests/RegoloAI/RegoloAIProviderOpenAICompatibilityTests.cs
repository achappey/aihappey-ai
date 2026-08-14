using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.RegoloAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.RegoloAI;

public sealed class RegoloAIProviderOpenAICompatibilityTests
{
    [Fact]
    public async Task OpenAITranscriptionRequest_sends_multipart_and_maps_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""{ "text": "hello from Regolo" }""");
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "faster-whisper-large-v3",
            File = CreateAudioFile(),
            Language = "en"
        });

        Assert.Equal("hello from Regolo", response.Text);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/v1/audio/transcriptions", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);
        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=file", body);
        Assert.Contains("filename=audio.ogg", body);
        Assert.Contains("faster-whisper-large-v3", body);
        Assert.Contains("name=language", body);
        Assert.Contains("en", body);
        Assert.DoesNotContain("name=stream", body);
    }

    [Fact]
    public async Task OpenAITranscriptionStreaming_emits_delta_and_done_from_synchronous_response()
    {
        var provider = CreateProvider(_ => JsonResponse("""{ "text": "hello from Regolo" }"""));
        var events = new List<IOpenAITranscriptionStreamEvent>();

        await foreach (var streamEvent in provider.OpenAITranscriptionStreamingAsync(new OpenAITranscriptionRequest
                       {
                           Model = "faster-whisper-large-v3",
                           File = CreateAudioFile()
                       }))
        {
            events.Add(streamEvent);
        }

        Assert.Collection(
            events,
            first => Assert.Equal("hello from Regolo", Assert.IsType<OpenAITranscriptionTextDelta>(first).Delta),
            second => Assert.Equal("hello from Regolo", Assert.IsType<OpenAITranscriptionTextDone>(second).Text));
    }

    [Fact]
    public async Task OpenAIImageGeneration_sends_compatible_payload_and_maps_response()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""{ "created": 1730000000, "data": [{ "b64_json": "aW1hZ2U=" }] }""");
        });

        var response = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "Qwen-Image",
            Prompt = "A boat in the sea",
            N = 1,
            Size = "1024x1024",
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["aspect_ratio"] = JsonSerializer.SerializeToElement("16:9")
            }
        });

        Assert.Equal(1730000000, response.Created);
        Assert.Equal("aW1hZ2U=", Assert.Single(response.Data!).B64Json);
        Assert.NotNull(capturedRequest);
        Assert.Equal("/v1/images/generations", capturedRequest!.RequestUri?.AbsolutePath);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);
        using var body = JsonDocument.Parse(await capturedRequest.Content!.ReadAsStringAsync());
        Assert.Equal("Qwen-Image", body.RootElement.GetProperty("model").GetString());
        Assert.Equal("A boat in the sea", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("16:9", body.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.False(body.RootElement.TryGetProperty("stream", out _));
    }

    [Fact]
    public async Task OpenAIImageGenerationStreaming_emits_completed_event_from_synchronous_response()
    {
        var provider = CreateProvider(_ => JsonResponse("""{ "created": 1730000000, "data": [{ "b64_json": "aW1hZ2U=" }] }"""));
        var events = new List<IOpenAIImageStreamEvent>();

        await foreach (var streamEvent in provider.OpenAIImageGenerationStreamingAsync(new OpenAIImageGenerationRequest
                       {
                           Model = "Qwen-Image",
                           Prompt = "A boat in the sea",
                           Size = "1024x1024"
                       }))
        {
            events.Add(streamEvent);
        }

        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("aW1hZ2U=", completed.B64Json);
        Assert.Equal(1730000000, completed.CreatedAt);
        Assert.Equal("1024x1024", completed.Size);
    }

    [Fact]
    public async Task ImageRequest_forwards_documented_aspect_ratio_without_warning()
    {
        string? requestJson = null;
        var provider = CreateProvider(request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{ "data": [{ "b64_json": "aW1hZ2U=" }] }""");
        });

        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "Qwen-Image",
            Prompt = "A cinematic boat",
            N = 1,
            AspectRatio = "16:9"
        });

        using var body = JsonDocument.Parse(requestJson!);
        Assert.Equal("16:9", body.RootElement.GetProperty("aspect_ratio").GetString());
        Assert.Empty(response.Warnings ?? []);
    }

    [Fact]
    public async Task OpenAIImageEdits_are_explicitly_unsupported()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("No HTTP request expected."));
        var request = new OpenAIImageEditRequest { Model = "Qwen-Image", Prompt = "Edit it" };

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(request));
        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditStreamingAsync(request));
    }

    [Fact]
    public async Task Compatible_provider_errors_include_status_and_body()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("unsupported image size", Encoding.UTF8, "application/json")
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
            {
                Model = "Qwen-Image",
                Prompt = "A boat"
            }));

        Assert.Contains("400", exception.Message);
        Assert.Contains("unsupported image size", exception.Message);
    }

    private static IFormFile CreateAudioFile()
    {
        var audio = Encoding.UTF8.GetBytes("fake audio");
        return new FormFile(new MemoryStream(audio, writable: false), 0, audio.Length, "file", "audio.ogg")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/ogg"
        };
    }

    private static RegoloAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        return new RegoloAIProvider(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(handler)));
    }

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;
        if (request.Content is not null)
        {
            clone.Content = new ByteArrayContent(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
            foreach (var header in request.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
