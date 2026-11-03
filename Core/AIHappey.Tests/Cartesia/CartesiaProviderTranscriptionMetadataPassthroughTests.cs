using System.Net;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Cartesia;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cartesia;

public sealed class CartesiaProviderTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_preserves_provider_options_and_forwards_unknown_fields()
    {
        using var providerOptionsDocument = JsonDocument.Parse("""
            {
              "apiVersion": "2026-09-10",
              "language": "de",
              "encoding": "pcm_f32le",
              "sampleRate": 22050,
              "timestampGranularities": ["word", "segment"],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var providerOptions = providerOptionsDocument.RootElement.Clone();
        var request = CreateRequest(providerOptions);
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Dictionary<string, List<string>>? capturedFields = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal("/stt", message.RequestUri?.AbsolutePath);
            Assert.Equal("encoding=pcm_f32le&sample_rate=22050", message.RequestUri?.Query.TrimStart('?'));
            Assert.Equal("2026-09-10", message.Headers.GetValues("Cartesia-Version").Single());
            capturedFields = ReadMultipartFields(message.Content!);
            return JsonResponse("{\"text\":\"transcribed\"}");
        });

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("transcribed", response.Text);
        Assert.True(JsonElement.DeepEquals(providerOptions,
            clientRequest.GetProperty("providerOptions").GetProperty("cartesia")));
        Assert.NotNull(capturedFields);
        Assert.Equal(new[] { "word", "segment" }, capturedFields["timestamp_granularities[]"]);
        Assert.Equal("de", Assert.Single(capturedFields["language"]));
        Assert.Equal("{\"nested\":[{\"value\":1},[true,null,\"text\"]]}",
            Assert.Single(capturedFields["future_object"]));
        Assert.Equal("[1,{\"more\":[false,2.5]},[]]", Assert.Single(capturedFields["future_array"]));
        Assert.Equal("unchanged", Assert.Single(capturedFields["future_string"]));
        Assert.Equal("12345678901234567890.123456789", Assert.Single(capturedFields["future_number"]));
        Assert.Equal("false", Assert.Single(capturedFields["future_boolean"]));
        Assert.Equal("null", Assert.Single(capturedFields["future_null"]));
        Assert.Equal("ink-whisper", Assert.Single(capturedFields["model"]));
        Assert.DoesNotContain("providerOptions", capturedFields.Keys);
        Assert.DoesNotContain("another-provider", capturedFields.Keys);
        Assert.DoesNotContain("apiVersion", capturedFields.Keys);
        Assert.DoesNotContain("encoding", capturedFields.Keys);
        Assert.DoesNotContain("sampleRate", capturedFields.Keys);
        Assert.DoesNotContain("timestampGranularities", capturedFields.Keys);
    }

    [Fact]
    public async Task Transcription_keeps_default_version_and_omits_null_mapped_options()
    {
        Dictionary<string, List<string>>? capturedFields = null;
        var provider = CreateProvider(message =>
        {
            Assert.Equal("2026-08-14", message.Headers.GetValues("Cartesia-Version").Single());
            Assert.Equal(string.Empty, message.RequestUri?.Query);
            capturedFields = ReadMultipartFields(message.Content!);
            return JsonResponse("{\"text\":\"transcribed\"}");
        });

        await provider.TranscriptionRequest(CreateRequest(ParseElement("""
            {
              "apiVersion": null,
              "language": null,
              "encoding": null,
              "sampleRate": null,
              "timestampGranularities": null,
              "future_null": null
            }
            """)));

        Assert.NotNull(capturedFields);
        Assert.Equal("null", Assert.Single(capturedFields["future_null"]));
        Assert.DoesNotContain("language", capturedFields.Keys);
        Assert.DoesNotContain("timestamp_granularities[]", capturedFields.Keys);
        Assert.Equal(2, capturedFields.Count);
    }

    [Fact]
    public async Task Transcription_keeps_invalid_known_metadata_and_upstream_error_behavior()
    {
        var unusedProvider = CreateProvider(_ => throw new Exception("Request should not be sent."));
        await Assert.ThrowsAsync<JsonException>(() => unusedProvider.TranscriptionRequest(
            CreateRequest(ParseElement("""{"sampleRate":"fast"}"""))));
        await Assert.ThrowsAsync<JsonException>(() => unusedProvider.TranscriptionRequest(
            CreateRequest(ParseElement("""{"timestampGranularities":[1]}"""))));

        var failingProvider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingProvider.TranscriptionRequest(CreateRequest()));
        Assert.Contains("Cartesia STT failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement? providerOptions = null)
    {
        var request = new TranscriptionRequest
        {
            Model = "ink-whisper",
            Audio = "AQID",
            MediaType = "audio/wav"
        };
        if (providerOptions is not null)
        {
            request.ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cartesia"] = providerOptions.Value,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            };
        }

        return request;
    }

    private static Dictionary<string, List<string>> ReadMultipartFields(HttpContent content)
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var multipart = Assert.IsType<MultipartFormDataContent>(content);
        foreach (var part in multipart)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (name is null || name == "file")
                continue;

            if (!fields.TryGetValue(name, out var values))
                fields[name] = values = [];
            values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
        }

        return fields;
    }

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json) };

    private static CartesiaProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new CartesiaProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

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
