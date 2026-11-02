using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Soniox;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Soniox;

public sealed class SonioxProviderTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_request_forwards_keyed_metadata_at_upstream_root_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v1/files")
            {
                var upload = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Assert.Contains("client_reference_id", upload);
                Assert.Contains("client-123", upload);
                return JsonResponse("""{"id":"file-1"}""");
            }

            if (request.Method == HttpMethod.Post && path == "/v1/transcriptions")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"id":"transcription-1"}""");
            }

            if (request.Method == HttpMethod.Get && path == "/v1/transcriptions/transcription-1")
                return JsonResponse("""{"status":"completed","audio_duration_ms":1000}""");

            if (request.Method == HttpMethod.Get && path == "/v1/transcriptions/transcription-1/transcript")
                return JsonResponse("""{"text":"hello","tokens":[{"text":"hello","start_ms":0,"end_ms":1000}]}""");

            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var metadata = ParseElement("""
            {
              "language_hints": ["en", "fr"],
              "language_hints_strict": true,
              "enable_speaker_diarization": false,
              "enable_language_identification": true,
              "context": {"general": {"topics": ["news", null, {"name": "kept"}]}},
              "client_reference_id": "client-123",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "wrong-model",
              "file_id": "wrong-file"
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "soniox/async-model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["soniox"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        await CreateProvider(client).TranscriptionRequest(request);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            serializedClientRequest.GetProperty("providerOptions").GetProperty("soniox")));
        Assert.Equal("async-model", payload.GetProperty("model").GetString());
        Assert.Equal("file-1", payload.GetProperty("file_id").GetString());
        Assert.Equal(2, payload.GetProperty("language_hints").GetArrayLength());
        Assert.True(payload.GetProperty("language_hints_strict").GetBoolean());
        Assert.False(payload.GetProperty("enable_speaker_diarization").GetBoolean());
        Assert.True(payload.GetProperty("enable_language_identification").GetBoolean());
        Assert.True(JsonElement.DeepEquals(
            metadata.GetProperty("context"),
            payload.GetProperty("context")));

        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean",
                     "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal("client-123", payload.GetProperty("client_reference_id").GetString());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("soniox", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("must_not_leak", out _));
        Assert.Equal(14, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Transcription_request_keeps_known_omissions_validation_and_upstream_errors()
    {
        JsonElement payload = default;
        var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/files")
                return JsonResponse("""{"id":"file-1"}""");
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/v1/transcriptions")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"id":"transcription-1"}""");
            }
            if (request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/v1/transcriptions/transcription-1")
                return JsonResponse("""{"status":"completed"}""");
            if (request.Method == HttpMethod.Get)
                return JsonResponse("""{"text":"hello","tokens":[]}""");
            if (request.Method == HttpMethod.Delete)
                return new HttpResponseMessage(HttpStatusCode.NoContent);

            throw new InvalidOperationException($"Unexpected request: {request.Method} {request.RequestUri}");
        });
        var request = CreateRequest(ParseElement("""
            {
              "language_hints": null,
              "language_hints_strict": null,
              "enable_speaker_diarization": null,
              "enable_language_identification": null,
              "context": null,
              "client_reference_id": null,
              "future_null": null,
              "model": "wrong-model",
              "file_id": "wrong-file"
            }
            """));

        await CreateProvider(client).TranscriptionRequest(request);

        Assert.Equal("async-model", payload.GetProperty("model").GetString());
        Assert.Equal("file-1", payload.GetProperty("file_id").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());

        var requestsSent = 0;
        var invalidClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(invalidClient).TranscriptionRequest(
            CreateRequest(ParseElement("""{"language_hints":[true]}"""))));
        Assert.Equal(0, requestsSent);

        var failedClient = CreateClient(requestMessage =>
        {
            if (requestMessage.Method == HttpMethod.Post && requestMessage.RequestUri!.AbsolutePath == "/v1/files")
                return JsonResponse("""{"id":"file-1"}""");
            if (requestMessage.Method == HttpMethod.Post && requestMessage.RequestUri!.AbsolutePath == "/v1/transcriptions")
                return JsonResponse("upstream failure", HttpStatusCode.BadRequest);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest(default)));
        Assert.Equal("Soniox transcription creation failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "soniox/async-model",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["soniox"] = metadata }
        };

    private static SonioxProvider CreateProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(responder(request));
        }
    }
}
