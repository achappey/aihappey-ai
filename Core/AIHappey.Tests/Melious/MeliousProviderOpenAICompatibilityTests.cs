using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Melious;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Melious;

public sealed class MeliousProviderOpenAICompatibilityTests
{
    [Fact]
    public async Task ImageGenerationForwardsDocumentedFieldsAndAdditionalProperties()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = CloneRequest(request);
            return Response("""{"created":1730000000,"data":[{"b64_json":"aW1hZ2U=","revised_prompt":"revised"}],"energy_cost":400}""");
        });

        var result = await provider.OpenAIImageGenerationRequestAsync(new OpenAIImageGenerationRequest
        {
            Model = "flux-model",
            Prompt = "A ship",
            N = 2,
            Size = "1024x1024",
            Quality = "hd",
            Style = "natural",
            User = "user-1",
            AdditionalProperties = new() { ["steps"] = JsonSerializer.SerializeToElement(28) }
        });

        Assert.Equal(1730000000, result.Created);
        var image = Assert.Single(result.Data!);
        Assert.Equal("aW1hZ2U=", image.B64Json);
        Assert.Equal("revised", image.RevisedPrompt);
        Assert.Equal("Bearer", captured!.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", captured.Headers.Authorization?.Parameter);
        Assert.Equal("/v1/images/generations", captured.RequestUri?.AbsolutePath);
        using var body = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync());
        Assert.Equal("b64_json", body.RootElement.GetProperty("response_format").GetString());
        Assert.Equal(28, body.RootElement.GetProperty("steps").GetInt32());
        Assert.Equal("natural", body.RootElement.GetProperty("style").GetString());
    }

    [Fact]
    public async Task ImageStreamingMimicsSynchronousCompletionAndEditsAreUnsupported()
    {
        var provider = CreateProvider(_ => Response("""{"created":1730000000,"data":[{"b64_json":"aW1hZ2U="}]}"""));
        var events = new List<IOpenAIImageStreamEvent>();
        await foreach (var item in provider.OpenAIImageGenerationStreamingAsync(new()
                       { Model = "flux-model", Prompt = "A ship", Size = "1024x1024" }))
            events.Add(item);

        var completed = Assert.IsType<OpenAIImageGenerationCompleted>(Assert.Single(events));
        Assert.Equal("aW1hZ2U=", completed.B64Json);
        Assert.Equal(1730000000, completed.CreatedAt);

        var edit = new OpenAIImageEditRequest { Model = "flux-model", Prompt = "Edit" };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(edit));
        Assert.Throws<NotSupportedException>(() => provider.OpenAIImageEditStreamingAsync(edit));
    }

    [Fact]
    public async Task TranscriptionForwardsMultipartAndPreservesVerboseJson()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = CloneRequest(request);
            return Response("""{"language":"de","duration":2.1,"text":"Hallo","segments":[],"words":[]}""");
        });

        var response = await provider.OpenAITranscriptionRequestAsync(new OpenAITranscriptionRequest
        {
            Model = "whisper-model",
            File = AudioFile(),
            Language = "de",
            ResponseFormat = "verbose_json",
            Temperature = 0.25f,
            AdditionalProperties = new() { ["custom"] = JsonSerializer.SerializeToElement("value") }
        });

        var verbose = Assert.IsType<OpenAITranscriptionVerboseResponse>(response);
        Assert.Equal("Hallo", verbose.Text);
        Assert.Equal("de", verbose.Language);
        Assert.Equal(2.1, verbose.Duration);
        var form = await captured!.Content!.ReadAsStringAsync();
        Assert.Equal("/v1/audio/transcriptions", captured.RequestUri?.AbsolutePath);
        Assert.Contains("name=language", form);
        Assert.Contains("verbose_json", form);
        Assert.Contains("name=custom", form);
        Assert.Contains("value", form);
    }

    [Theory]
    [InlineData("text", "plain transcript")]
    [InlineData("srt", "1\n00:00:00,000 --> 00:00:01,000\nHello")]
    [InlineData("vtt", "WEBVTT\n\n00:00.000 --> 00:01.000\nHello")]
    public async Task TranscriptionNormalizesTextFormatsAndMimicsStreaming(string format, string body)
    {
        var provider = CreateProvider(_ => Response(body, "text/plain"));
        var request = new OpenAITranscriptionRequest { Model = "whisper-model", File = AudioFile(), ResponseFormat = format };
        var response = await provider.OpenAITranscriptionRequestAsync(request);
        Assert.Equal(body, response.Text);

        var events = new List<IOpenAITranscriptionStreamEvent>();
        await foreach (var item in provider.OpenAITranscriptionStreamingAsync(request))
            events.Add(item);
        Assert.Collection(events,
            item => Assert.Equal(body, Assert.IsType<OpenAITranscriptionTextDelta>(item).Delta),
            item => Assert.Equal(body, Assert.IsType<OpenAITranscriptionTextDone>(item).Text));
    }

    [Fact]
    public async Task ProviderErrorsIncludeStatusAndBody()
    {
        var provider = CreateProvider(_ => Response("bad request", "text/plain", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIImageGenerationRequestAsync(
            new OpenAIImageGenerationRequest { Model = "flux-model", Prompt = "A ship" }));
        Assert.Contains("400", error.Message);
        Assert.Contains("bad request", error.Message);
    }

    private static IFormFile AudioFile()
    {
        byte[] bytes = [1, 2, 3];
        return new FormFile(new MemoryStream(bytes, writable: false), 0, bytes.Length, "file", "audio.mp3")
        {
            Headers = new HeaderDictionary(),
            ContentType = "audio/mpeg"
        };
    }

    private static MeliousProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new KeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Response(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

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

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
