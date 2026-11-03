using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.RelaxAI;
using AIHappey.Core.Providers.Zai;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderMetadataRawTranscriptionPassthroughTests
{
    [Fact]
    public async Task Relaxai_transcription_keeps_keyed_contract_and_forwards_unknown_multipart_fields()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var client = CreateClient(request =>
        {
            Assert.Equal("/v1/audio/transactions", request.RequestUri!.AbsolutePath);
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"transcribed"}""");
        });

        var metadata = ParseElement(
            """
            {
              "known_speaker_names": ["speaker-a", " "],
              "known_speaker_references": ["ref-a"],
              "language": "en",
              "prompt": "context",
              "response_format": "json",
              "temperature": 0.25,
              "timestamp_granularities": ["word", "segment"],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "file": "metadata-file"
            }
            """);
        var request = CreateRequest("whisper-1", metadata, "relaxai");
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("relaxai")));

        var response = await CreateRelaxAIProvider(client).TranscriptionRequest(request, CancellationToken.None);

        Assert.Equal("transcribed", response.Text);
        Assert.Equal("whisper-1", Single(fields, "model"));
        Assert.Equal("audio", Single(fields, "file"));
        Assert.Equal("en", Single(fields, "language"));
        Assert.Equal("context", Single(fields, "prompt"));
        Assert.Equal("json", Single(fields, "response_format"));
        Assert.Equal("0.25", Single(fields, "temperature"));
        Assert.Equal(["word", "segment"], fields["timestamp_granularities"]);
        Assert.Equal("speaker-a", Single(fields, "known_speaker_names"));
        Assert.Equal("ref-a", Single(fields, "known_speaker_references"));
        Assert.Equal("""{"nested":[{"value":1},[true,null,"text"]]}""", Single(fields, "future_object"));
        Assert.Equal("""[1,{"more":[false,2.5]},[]]""", Single(fields, "future_array"));
        Assert.Equal("unchanged", Single(fields, "future_string"));
        Assert.Equal("12345678901234567890.123456789", Single(fields, "future_number"));
        Assert.Equal("false", Single(fields, "future_boolean"));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.Equal(1, fields["model"].Count);
        Assert.Equal(1, fields["file"].Count);
        Assert.Equal(15, fields.Count);
        Assert.DoesNotContain("providerOptions", fields.Keys);
        Assert.DoesNotContain("relaxai", fields.Keys);
        Assert.DoesNotContain("must_not_leak", fields.Keys);
    }

    [Fact]
    public async Task Relaxai_transcription_keeps_default_omissions_validation_and_upstream_errors()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var client = CreateClient(request =>
        {
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"transcribed"}""");
        });

        await CreateRelaxAIProvider(client).TranscriptionRequest(CreateRequest("whisper-1", default, "relaxai"), CancellationToken.None);

        Assert.Equal("verbose_json", Single(fields, "response_format"));
        Assert.False(fields.ContainsKey("language"));
        Assert.False(fields.ContainsKey("prompt"));
        Assert.False(fields.ContainsKey("temperature"));
        Assert.False(fields.ContainsKey("timestamp_granularities"));

        fields.Clear();
        await CreateRelaxAIProvider(client).TranscriptionRequest(CreateRequest(
            "whisper-1",
            ParseElement("""{"language":null,"prompt":" ","response_format":null,"temperature":null,"future_null":null}"""),
            "relaxai"), CancellationToken.None);

        Assert.Equal("verbose_json", Single(fields, "response_format"));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.False(fields.ContainsKey("language"));
        Assert.False(fields.ContainsKey("prompt"));
        Assert.False(fields.ContainsKey("temperature"));
        Assert.Equal(4, fields.Count);

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<JsonException>(() =>
            CreateRelaxAIProvider(unusedClient).TranscriptionRequest(
                CreateRequest("whisper-1", ParseElement("""{"language":42}"""), "relaxai"), CancellationToken.None));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateRelaxAIProvider(failedClient).TranscriptionRequest(CreateRequest("whisper-1", default, "relaxai"), CancellationToken.None));
        Assert.Equal("RelaxAI STT failed (400): upstream failure", error.Message);
    }

    [Fact]
    public async Task Zai_transcription_keeps_keyed_contract_and_forwards_unknown_multipart_fields()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var client = CreateClient(request =>
        {
            Assert.Equal("/api/paas/v4/audio/transcriptions", request.RequestUri!.AbsolutePath);
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"transcribed"}""");
        });

        var metadata = ParseElement(
            """
            {
              "prompt": "context",
              "hotwords": ["speaker-a", "speaker-b"],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "file": "metadata-file",
              "stream": true
            }
            """);
        var request = CreateRequest("glm-asr", metadata, "zai");
        var clientBoundary = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);

        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientBoundary.GetProperty("providerOptions").GetProperty("zai")));

        var response = await CreateZaiProvider(client).TranscriptionRequest(request);

        Assert.Equal("transcribed", response.Text);
        Assert.Equal("glm-asr", Single(fields, "model"));
        Assert.Equal("audio", Single(fields, "file"));
        Assert.Equal("context", Single(fields, "prompt"));
        Assert.Equal(["speaker-a", "speaker-b"], fields["hotwords"]);
        Assert.Equal("""{"nested":[{"value":1},[true,null,"text"]]}""", Single(fields, "future_object"));
        Assert.Equal("""[1,{"more":[false,2.5]},[]]""", Single(fields, "future_array"));
        Assert.Equal("unchanged", Single(fields, "future_string"));
        Assert.Equal("12345678901234567890.123456789", Single(fields, "future_number"));
        Assert.Equal("false", Single(fields, "future_boolean"));
        Assert.Equal("null", Single(fields, "future_null"));
        Assert.Equal("false", Single(fields, "stream"));
        Assert.Equal(1, fields["model"].Count);
        Assert.Equal(1, fields["file"].Count);
        Assert.Equal(11, fields.Count);
        Assert.DoesNotContain("providerOptions", fields.Keys);
        Assert.DoesNotContain("zai", fields.Keys);
        Assert.DoesNotContain("must_not_leak", fields.Keys);
    }

    [Fact]
    public async Task Zai_transcription_keeps_omissions_validation_and_upstream_errors()
    {
        var fields = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        using var client = CreateClient(request =>
        {
            CaptureFields(request, fields);
            return JsonResponse("""{"text":"transcribed"}""");
        });

        await CreateZaiProvider(client).TranscriptionRequest(CreateRequest("glm-asr", default, "zai"));

        Assert.Equal("false", Single(fields, "stream"));
        Assert.False(fields.ContainsKey("prompt"));
        Assert.False(fields.ContainsKey("hotwords"));

        var requestsSent = 0;
        using var unusedClient = CreateClient(_ =>
        {
            requestsSent++;
            throw new InvalidOperationException("Request should not be sent.");
        });
        await Assert.ThrowsAsync<JsonException>(() =>
            CreateZaiProvider(unusedClient).TranscriptionRequest(
                CreateRequest("glm-asr", ParseElement("""{"prompt":42}"""), "zai")));
        Assert.Equal(0, requestsSent);

        using var failedClient = CreateClient(_ => JsonResponse("upstream failure", HttpStatusCode.BadRequest));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateZaiProvider(failedClient).TranscriptionRequest(CreateRequest("glm-asr", default, "zai")));
        Assert.Equal("Z.AI STT failed (400): upstream failure", error.Message);
    }

    private static TranscriptionRequest CreateRequest(string model, JsonElement metadata, string provider)
        => new()
        {
            Model = model,
            Audio = Convert.ToBase64String(Encoding.UTF8.GetBytes("audio")),
            MediaType = "audio/wav",
            ProviderOptions = metadata.ValueKind == JsonValueKind.Undefined
                ? null
                : new Dictionary<string, JsonElement>
                {
                    [provider] = metadata,
                    ["another-provider"] = ParseElement("""{"must_not_leak":true}""")
                }
        };

    private static RelaxAIProvider CreateRelaxAIProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static ZaiProvider CreateZaiProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHandler(responder));

    private static void CaptureFields(HttpRequestMessage request, Dictionary<string, List<string>> fields)
    {
        var form = Assert.IsType<MultipartFormDataContent>(request.Content);
        foreach (var part in form)
        {
            var name = part.Headers.ContentDisposition?.Name?.Trim('"');
            if (string.IsNullOrEmpty(name))
                continue;

            if (!fields.TryGetValue(name, out var values))
                fields[name] = values = [];
            values.Add(part.ReadAsStringAsync().GetAwaiter().GetResult());
        }
    }

    private static string Single(Dictionary<string, List<string>> fields, string name)
        => Assert.Single(fields[name]);

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
