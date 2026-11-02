using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Speechmatics;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Speechmatics;

public sealed class SpeechmaticsProviderTranscriptionMetadataPassthroughTests
{
    [Fact]
    public async Task Transcription_request_preserves_keyed_provider_options_inside_upstream_config()
    {
        JsonElement config = default;
        using var client = CreateClient(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path == "/v2/jobs")
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                var configText = ReadMultipartField(body, "config");
                config = ParseElement(configText);
                return JsonResponse("""{"id":"job-1"}""");
            }

            if (request.Method == HttpMethod.Get && path == "/v2/jobs/job-1")
                return JsonResponse("""{"status":"done","job":{"duration":1.5}}""");

            if (request.Method == HttpMethod.Get && path == "/v2/jobs/job-1/transcript")
                return JsonResponse("""{"results":[{"type":"word","alternatives":[{"content":"hello"}],"start_time":0,"end_time":1}]}""");

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });

        var metadata = ParseElement("""
            {
              "language": "en",
              "operating_point": "enhanced",
              "diarization": "speaker",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "wrong-model"
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "speechmatics/standard",
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["speechmatics"] = metadata,
                ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
            }
        };
        var serializedClientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        await CreateProvider(client).TranscriptionRequest(request);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            serializedClientRequest.GetProperty("providerOptions").GetProperty("speechmatics")));
        Assert.Equal("transcription", config.GetProperty("type").GetString());
        var transcriptionConfig = config.GetProperty("transcription_config");
        Assert.Equal("standard", transcriptionConfig.GetProperty("model").GetString());
        Assert.Equal("en", transcriptionConfig.GetProperty("language").GetString());
        foreach (var name in new[]
                 {
                     "operating_point", "diarization", "future_object", "future_array", "future_string",
                     "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), transcriptionConfig.GetProperty(name)), name);
        }

        Assert.False(config.TryGetProperty("providerOptions", out _));
        Assert.False(config.TryGetProperty("speechmatics", out _));
        Assert.False(transcriptionConfig.TryGetProperty("another-provider", out _));
        Assert.False(transcriptionConfig.TryGetProperty("must_not_leak", out _));
        Assert.Equal(10, transcriptionConfig.EnumerateObject().Count());
    }

    [Fact]
    public async Task Transcription_request_preserves_language_defaults_overrides_validation_and_errors()
    {
        JsonElement config = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                config = ParseElement(ReadMultipartField(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), "config"));
                return JsonResponse("""{"id":"job-1"}""");
            }

            if (request.RequestUri!.AbsolutePath == "/v2/jobs/job-1")
                return JsonResponse("""{"status":"done"}""");

            return JsonResponse("""{"results":[]}""");
        });

        await CreateProvider(client).TranscriptionRequest(CreateRequest(
            "melia-1",
            ParseElement("""{"language":"en","future_null":null,"model":"wrong-model"}""")));

        var transcriptionConfig = config.GetProperty("transcription_config");
        Assert.Equal("melia-1", transcriptionConfig.GetProperty("model").GetString());
        Assert.Equal("multi", transcriptionConfig.GetProperty("language").GetString());
        Assert.Equal(JsonValueKind.Null, transcriptionConfig.GetProperty("future_null").ValueKind);
        Assert.Equal(3, transcriptionConfig.EnumerateObject().Count());

        JsonElement emptyConfig = default;
        using var emptyClient = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                emptyConfig = ParseElement(ReadMultipartField(
                    request.Content!.ReadAsStringAsync().GetAwaiter().GetResult(), "config"));
                return JsonResponse("""{"id":"job-1"}""");
            }

            if (request.RequestUri!.AbsolutePath == "/v2/jobs/job-1")
                return JsonResponse("""{"status":"done"}""");

            return JsonResponse("""{"results":[]}""");
        });
        await CreateProvider(emptyClient).TranscriptionRequest(CreateRequest("standard", ParseElement("{}")));
        var defaultConfig = emptyConfig.GetProperty("transcription_config");
        Assert.Equal("standard", defaultConfig.GetProperty("model").GetString());
        Assert.False(defaultConfig.TryGetProperty("language", out _));
        Assert.Equal(1, defaultConfig.EnumerateObject().Count());

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<JsonException>(() =>
            CreateProvider(unusedClient).TranscriptionRequest(CreateRequest("standard", ParseElement("""{"language":42}"""))));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(failedClient).TranscriptionRequest(CreateRequest("standard", default)));
        Assert.Equal(
            "Speechmatics transcription job creation failed (400): upstream failure",
            error.Message);
    }

    private static TranscriptionRequest CreateRequest(string model, JsonElement metadata)
        => new()
        {
            Model = "speechmatics/" + model,
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement> { ["speechmatics"] = metadata }
        };

    private static SpeechmaticsProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static string ReadMultipartField(string body, string fieldName)
    {
        var markerIndex = body.IndexOf($"name={fieldName}", StringComparison.Ordinal);
        if (markerIndex < 0)
            markerIndex = body.IndexOf($"name=\"{fieldName}\"", StringComparison.Ordinal);
        Assert.True(markerIndex >= 0, $"Multipart field {fieldName} was not found.");
        var valueStart = body.IndexOf("\r\n\r\n", markerIndex, StringComparison.Ordinal) + 4;
        var valueEnd = body.IndexOf("\r\n--", valueStart, StringComparison.Ordinal);
        Assert.True(valueStart >= 4 && valueEnd >= valueStart, $"Multipart field {fieldName} was malformed.");
        return body[valueStart..valueEnd];
    }

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
