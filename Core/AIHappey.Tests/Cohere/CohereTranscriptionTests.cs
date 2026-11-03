using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Cohere;

public sealed class CohereTranscriptionTests
{
    [Fact]
    public async Task Transcription_request_posts_multipart_to_cohere_v2_endpoint()
    {
        HttpRequestMessage? capturedRequest = null;

        var provider = CreateProvider(request =>
        {
            capturedRequest = CloneRequest(request);
            return JsonResponse("""
                {
                  "text": "hello from cohere"
                }
                """);
        });

        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = JsonSerializer.SerializeToElement(new
                {
                    language = "en",
                    temperature = 0.2f
                }, JsonSerializerOptions.Web)
            }
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v2/audio/transcriptions", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-api-key", capturedRequest.Headers.Authorization?.Parameter);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        Assert.Contains("name=model", body);
        Assert.Contains("cohere-transcribe-03-2026", body);
        Assert.Contains("name=language", body);
        Assert.Contains("en", body);
        Assert.Contains("name=temperature", body);
        Assert.Contains("0.2", body);
        Assert.Contains("name=file", body);

        Assert.Equal("hello from cohere", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Empty(response.Segments);
        Assert.Equal("cohere-transcribe-03-2026", response.Response.ModelId);
    }

    [Fact]
    public async Task Transcription_request_requires_cohere_language_provider_option()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav"
        }));

        Assert.Contains("providerOptions.cohere.language", ex.Message);
    }

    [Fact]
    public async Task Transcription_request_preserves_keyed_metadata_and_forwards_unknown_json_fields()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var metadata = JsonDocument.Parse("""
            {
              "language": "en",
              "temperature": 0.2,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "file": "metadata-file"
            }
            """).RootElement.Clone();
        using var client = CreateClient(request =>
        {
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"transcribed"}""");
        });
        var request = new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = metadata,
                ["another-provider"] = JsonDocument.Parse("""{"must_not_leak":true}""").RootElement.Clone()
            }
        };
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(metadata, clientBoundary.GetProperty("providerOptions").GetProperty("cohere")));

        var response = await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("transcribed", response.Text);
        Assert.Equal("en", response.Language);
        Assert.Equal("cohere-transcribe-03-2026", Single(fields, "model"));
        Assert.Equal("audio", Single(fields, "file"));
        Assert.Equal("en", Single(fields, "language"));
        Assert.Equal("0.2", Single(fields, "temperature"));
        Assert.Equal(metadata.GetProperty("future_object").GetRawText(), Single(fields, "future_object"));
        Assert.Equal(metadata.GetProperty("future_array").GetRawText(), Single(fields, "future_array"));
        Assert.Equal("unchanged", Single(fields, "future_string"));
        Assert.Equal("12345678901234567890.123456789", Single(fields, "future_number"));
        Assert.Equal("false", Single(fields, "future_boolean"));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.Equal(10, fields.Count);
        Assert.Equal(1, fields["model"].Count);
        Assert.Equal(1, fields["file"].Count);
        Assert.DoesNotContain("providerOptions", fields.Keys);
        Assert.DoesNotContain("cohere", fields.Keys);
        Assert.DoesNotContain("another-provider", fields.Keys);
    }

    [Fact]
    public async Task Transcription_request_preserves_known_validation_and_upstream_error_behavior()
    {
        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var invalidProvider = CreateProvider(invalidClient);
        var invalidRequest = new TranscriptionRequest
        {
            Model = "cohere-transcribe-03-2026",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["cohere"] = JsonDocument.Parse("""{"language":"en","temperature":"warm"}""").RootElement.Clone()
            }
        };
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(invalidRequest));

        using var failedClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(new TranscriptionRequest
            {
                Model = "cohere-transcribe-03-2026",
                Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("fake audio")),
                MediaType = "audio/wav",
                ProviderOptions = new Dictionary<string, JsonElement>
                {
                    ["cohere"] = JsonDocument.Parse("""{"language":"en"}""").RootElement.Clone()
                }
            }));

        Assert.Equal("Cohere transcription failed (400): upstream failure", error.Message);
    }

    private static CohereProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new StaticResponseHttpMessageHandler(responder);
        var httpClientFactory = new StaticHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.cohere.com/")
        });
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));

        return new CohereProvider(new StaticApiKeyResolver(), cache, httpClientFactory);
    }

    private static CohereProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "text/plain");
        }

        return clone;
    }

    private static void CaptureFields(HttpRequestMessage request, Dictionary<string, List<string>> fields)
    {
        var multipart = Assert.IsType<MultipartFormDataContent>(request.Content);

        foreach (var part in multipart)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (string.IsNullOrEmpty(name))
                continue;

            if (!fields.TryGetValue(name, out var values))
                fields[name] = values = [];

            values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
        }
    }

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticResponseHttpMessageHandler(responder))
        {
            BaseAddress = new Uri("https://api.cohere.com/")
        };

    private static string Single(Dictionary<string, List<string>> fields, string name)
        => Assert.Single(fields[name]);

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = responder(request);
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
