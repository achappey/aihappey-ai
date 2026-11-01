using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Infomaniak;
using AIHappey.Core.Providers.NLPCloud;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class ProviderMetadataTranscriptionPassthroughTests
{
    [Fact]
    public async Task Nlpcloud_transcription_preserves_provider_key_and_forwards_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/gpu/whisper/asr", request.RequestUri!.AbsolutePath);
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"text":"transcribed"}""");
        });

        var metadata = ParseElement("""
            {
              "input_language": "en",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "encoded_file": "metadata-audio",
              "other_property": "forwarded"
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "whisper/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["nlpcloud"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(clientRequest.GetProperty("providerOptions").TryGetProperty("nlpcloud", out _));
        Assert.False(clientRequest.TryGetProperty("nlpcloud", out _));

        await CreateNlpCloudProvider(client).TranscriptionRequest(request);

        Assert.Equal("transcribed", payload.GetProperty("text").GetString());
        Assert.Equal("en", payload.GetProperty("input_language").GetString());
        Assert.Equal(request.Audio!.ToString(), payload.GetProperty("encoded_file").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null",
                     "other_property"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("nlpcloud", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal("forwarded", payload.GetProperty("other_property").GetString());
        Assert.Equal(9, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Nlpcloud_transcription_keeps_omissions_validation_and_error_behavior()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"text":"transcribed"}""");
        });
        var provider = CreateNlpCloudProvider(client);
        var request = CreateNlpCloudRequest(ParseElement("""{"input_language":" ","future_null":null}"""));

        await provider.TranscriptionRequest(request);

        Assert.False(payload.TryGetProperty("input_language", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(2, payload.EnumerateObject().Count());

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        request = CreateNlpCloudRequest(ParseElement("""{"input_language":42}"""));
        await Assert.ThrowsAsync<JsonException>(() => CreateNlpCloudProvider(unusedClient).TranscriptionRequest(request));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateNlpCloudProvider(failedClient).TranscriptionRequest(CreateNlpCloudRequest(default)));
        Assert.Equal("NLPCloud transcription failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task Infomaniak_transcription_keeps_provider_contract_and_forwards_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/1/ai")
                return JsonResponse("""{"result":"success","data":[{"status":"ok","product_id":7}]}""");
            if (request.Method == HttpMethod.Post && path == "/1/ai/7/openai/audio/transcriptions")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"batch_id":"batch-1"}""");
            }
            if (request.Method == HttpMethod.Get && path == "/1/ai/7/results/batch-1")
                return JsonResponse("""{"status":"success","data":{"text":"transcribed"}}""");

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var metadata = ParseElement("""
            {
              "language": "en",
              "prompt": "context",
              "response_format": "verbose_json",
              "timestamp_granularities": ["word", "segment"],
              "append_punctuations": ".,",
              "prepend_punctuations": "¿",
              "chunk_length": 30,
              "highlight_words": true,
              "max_line_count": 4,
              "max_line_width": 42,
              "max_words_per_line": 8,
              "no_speech_threshold": 0.6,
              "file": "metadata-audio",
              "model": "metadata-model",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["infomaniak"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":true}""")
            }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(clientRequest.GetProperty("providerOptions").TryGetProperty("infomaniak", out _));
        Assert.False(clientRequest.TryGetProperty("infomaniak", out _));

        await CreateInfomaniakProvider(client).TranscriptionRequest(request);

        Assert.Equal("whisper-large-v3", payload.GetProperty("model").GetString());
        Assert.Equal(request.Audio!.ToString(), payload.GetProperty("file").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal("context", payload.GetProperty("prompt").GetString());
        Assert.Equal("verbose_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(2, payload.GetProperty("timestamp_granularities").GetArrayLength());
        Assert.Equal(".,", payload.GetProperty("append_punctuations").GetString());
        Assert.Equal("¿", payload.GetProperty("prepend_punctuations").GetString());
        Assert.Equal(30, payload.GetProperty("chunk_length").GetInt32());
        Assert.True(payload.GetProperty("highlight_words").GetBoolean());
        Assert.Equal(4, payload.GetProperty("max_line_count").GetInt32());
        Assert.Equal(42, payload.GetProperty("max_line_width").GetInt32());
        Assert.Equal(8, payload.GetProperty("max_words_per_line").GetInt32());
        Assert.Equal(0.6, payload.GetProperty("no_speech_threshold").GetDouble());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("infomaniak", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(20, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Infomaniak_transcription_keeps_omissions_validation_and_error_behavior()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path == "/1/ai")
                return JsonResponse("""{"result":"success","data":[{"status":"ok","product_id":7}]}""");
            if (request.Method == HttpMethod.Post && path == "/1/ai/7/openai/audio/transcriptions")
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"batch_id":"batch-1"}""");
            }
            if (request.Method == HttpMethod.Get && path == "/1/ai/7/results/batch-1")
                return JsonResponse("""{"status":"success","data":{"text":"transcribed"}}""");

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });
        var request = CreateInfomaniakRequest(ParseElement(
            """{"language":null,"prompt":"","timestamp_granularities":[],"chunk_length":null,"future_null":null}"""));

        await CreateInfomaniakProvider(client).TranscriptionRequest(request);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("language", out _));
        Assert.False(payload.TryGetProperty("prompt", out _));
        Assert.False(payload.TryGetProperty("timestamp_granularities", out _));
        Assert.False(payload.TryGetProperty("chunk_length", out _));
        Assert.Equal(3, payload.EnumerateObject().Count());

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        request = CreateInfomaniakRequest(ParseElement("""{"chunk_length":"invalid"}"""));
        await Assert.ThrowsAsync<JsonException>(() =>
            CreateInfomaniakProvider(unusedClient).TranscriptionRequest(request));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(requestMessage =>
        {
            var path = requestMessage.RequestUri!.AbsolutePath;
            return path == "/1/ai"
                ? JsonResponse("""{"result":"success","data":[{"status":"ok","product_id":7}]}""")
                : JsonResponse("upstream failure", HttpStatusCode.BadRequest);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateInfomaniakProvider(failedClient).TranscriptionRequest(CreateInfomaniakRequest(default)));
        Assert.Contains("Infomaniak transcription create failed: 400", error.Message);
        Assert.Contains("upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateNlpCloudRequest(JsonElement metadata)
        => new()
        {
            Model = "whisper/asr",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["nlpcloud"] = metadata }
        };

    private static TranscriptionRequest CreateInfomaniakRequest(JsonElement metadata)
        => new()
        {
            Model = "whisper-large-v3",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["infomaniak"] = metadata }
        };

    private static NLPCloudProvider CreateNlpCloudProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static InfomaniakProvider CreateInfomaniakProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(
        string json,
        HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
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
