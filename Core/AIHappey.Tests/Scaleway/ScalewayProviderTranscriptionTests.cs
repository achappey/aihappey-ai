using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Scaleway;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Scaleway;

public sealed class ScalewayProviderTranscriptionTests
{
    [Fact]
    public async Task TranscriptionForwardsKnownAndUnknownProviderOptionsAsMultipartFields()
    {
        using var providerOptions = JsonDocument.Parse("""
            {
              "language": "nl",
              "prompt": "Keep punctuation.",
              "temperature": 0.25,
              "nested": { "items": [true, null, 3] },
              "array": ["word", 2, false],
              "enabled": false,
              "count": 7,
              "label": "future-option",
              "explicit_null": null,
              "model": "metadata-model",
              "file": "metadata-file"
            }
            """);
        using var otherProviderOptions = JsonDocument.Parse("""{"must_not_forward":true}""");
        var handler = new RecordingHandler(JsonResponse("""{"text":"Transcript"}"""));
        var provider = CreateProvider(handler);
        var request = new TranscriptionRequest
        {
            Model = "scaleway/whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["scaleway"] = providerOptions.RootElement.Clone(),
                ["another-provider"] = otherProviderOptions.RootElement.Clone()
            }
        };

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("Transcript", response.Text);
        Assert.Contains("scaleway", request.ProviderOptions.Keys);
        Assert.Contains("another-provider", request.ProviderOptions.Keys);

        var recorded = Assert.Single(handler.Requests);
        Assert.Equal("/v1/audio/transcriptions", recorded.Uri?.AbsolutePath);
        var fields = Assert.IsType<Dictionary<string, string[]>>(recorded.MultipartFields);
        Assert.Equal("scaleway/whisper-large-v3", Assert.Single(fields["model"]));
        Assert.Equal("nl", Assert.Single(fields["language"]));
        Assert.Equal("Keep punctuation.", Assert.Single(fields["prompt"]));
        Assert.Equal("0.25", Assert.Single(fields["temperature"]));
        AssertJsonEqual("""{"items":[true,null,3]}""", Assert.Single(fields["nested"]));
        AssertJsonEqual("""["word",2,false]""", Assert.Single(fields["array"]));
        Assert.Equal("false", Assert.Single(fields["enabled"]));
        Assert.Equal("7", Assert.Single(fields["count"]));
        Assert.Equal("future-option", Assert.Single(fields["label"]));
        Assert.Equal("null", Assert.Single(fields["explicit_null"]));
        Assert.Equal(
            new[] { "array", "count", "enabled", "explicit_null", "file", "label", "language", "model", "nested", "prompt", "temperature" },
            fields.Keys.Order().ToArray());
        Assert.DoesNotContain("another-provider", fields.Keys);
        Assert.DoesNotContain("providerOptions", fields.Keys);
        Assert.DoesNotContain("providerMetadata", fields.Keys);
        Assert.DoesNotContain("metadata-model", fields.Values.SelectMany(values => values));
        Assert.DoesNotContain("metadata-file", fields.Values.SelectMany(values => values));
        Assert.Single(fields["file"]);
    }

    [Fact]
    public async Task TranscriptionOmitsEmptyKnownOptionsAndPreservesMetadataValidation()
    {
        var handler = new RecordingHandler(JsonResponse("""{"text":"Transcript"}"""));
        var provider = CreateProvider(handler);
        using var providerOptions = JsonDocument.Parse("""
            {"language":" ","prompt":null,"temperature":null}
            """);
        var request = CreateRequest(providerOptions.RootElement.Clone());

        await provider.TranscriptionRequest(request);

        var fields = Assert.IsType<Dictionary<string, string[]>>(Assert.Single(handler.Requests).MultipartFields);
        Assert.Equal(new[] { "file", "model" }, fields.Keys.Order().ToArray());

        var invalidProviderHandler = new RecordingHandler(JsonResponse("""{"text":"Transcript"}"""));
        var invalidProvider = CreateProvider(invalidProviderHandler);
        using var invalidOptions = JsonDocument.Parse("""{"language":42}""");
        var invalidRequest = CreateRequest(invalidOptions.RootElement.Clone());

        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(invalidRequest));
        Assert.Empty(invalidProviderHandler.Requests);
    }

    [Fact]
    public async Task TranscriptionPreservesUpstreamErrorBehavior()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""{"error":"invalid audio"}""", Encoding.UTF8, "application/json")
        });
        var provider = CreateProvider(handler);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.TranscriptionRequest(CreateRequest()));

        Assert.Equal("Scaleway STT failed (400): {\"error\":\"invalid audio\"}", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement? providerOptions = null)
    {
        var request = new TranscriptionRequest
        {
            Model = "scaleway/whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav"
        };

        if (providerOptions is not null)
            request.ProviderOptions = new Dictionary<string, JsonElement> { ["scaleway"] = providerOptions.Value };

        return request;
    }

    private static void AssertJsonEqual(string expected, string actual)
    {
        using var expectedDocument = JsonDocument.Parse(expected);
        using var actualDocument = JsonDocument.Parse(actual);
        Assert.True(JsonElement.DeepEquals(expectedDocument.RootElement, actualDocument.RootElement));
    }

    private static ScalewayProvider CreateProvider(RecordingHandler handler)
        => new(
            new FixedApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new FixedHttpClientFactory(new HttpClient(handler) { BaseAddress = new Uri("https://api.scaleway.ai/") }));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FixedApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);

        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Dictionary<string, string[]>? multipartFields = null;
            if (request.Content is MultipartFormDataContent multipart)
            {
                var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (!fields.TryGetValue(name, out var values))
                        fields[name] = values = [];
                    values.Add(await part.ReadAsStringAsync(cancellationToken));
                }

                multipartFields = fields.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                multipartFields));

            Assert.True(responses.TryDequeue(out var response), $"No response queued for {request.Method} {request.RequestUri}.");
            return response;
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Body,
        Dictionary<string, string[]>? MultipartFields);
}
