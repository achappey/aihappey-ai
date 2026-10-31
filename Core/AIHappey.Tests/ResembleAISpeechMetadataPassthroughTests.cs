using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.ResembleAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ResembleAISpeechMetadataPassthroughTests
{
    [Fact]
    public async Task ResembleAI_merges_raw_provider_options_at_the_request_root_and_preserves_known_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("https://f.cluster.resemble.ai/synthesize", request.RequestUri!.ToString());
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"audio_content":"AQ==","output_format":"mp3"}""");
            }

            Assert.Equal("/api/v2/voices?page_size=1000&page=1", request.RequestUri!.PathAndQuery);
            return JsonResponse("""{"items":[],"page":1,"num_pages":1}""");
        });
        var metadata = ParseElement("""
            {
              "voice_uuid": "metadata-voice",
              "project_uuid": "project-1",
              "title": "clip title",
              "precision": "PCM_16",
              "output_format": "wav",
              "sample_rate": 22050,
              "use_hd": true,
              "apply_custom_pronunciations": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "data": "metadata text",
              "model": "metadata model"
            }
            """);
        var request = new SpeechRequest
        {
            Model = "chatterbox",
            Text = "request text",
            Voice = "request-voice",
            OutputFormat = "mpeg",
            ProviderOptions = new()
            {
                ["resembleai"] = metadata,
                ["other-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("request text", payload.GetProperty("data").GetString());
        Assert.Equal("chatterbox", payload.GetProperty("model").GetString());
        Assert.Equal("request-voice", payload.GetProperty("voice_uuid").GetString());
        Assert.Equal("mp3", payload.GetProperty("output_format").GetString());
        Assert.Equal("project-1", payload.GetProperty("project_uuid").GetString());
        Assert.Equal("clip title", payload.GetProperty("title").GetString());
        Assert.Equal("PCM_16", payload.GetProperty("precision").GetString());
        Assert.Equal(22050, payload.GetProperty("sample_rate").GetInt32());
        Assert.True(payload.GetProperty("use_hd").GetBoolean());
        Assert.False(payload.GetProperty("apply_custom_pronunciations").GetBoolean());
        AssertUnknownFieldsForwarded(metadata, payload,
            "voice_uuid", "project_uuid", "title", "precision", "output_format",
            "sample_rate", "use_hd", "apply_custom_pronunciations", "data", "model");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("resembleai", out _));
        Assert.False(payload.TryGetProperty("other-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["resembleai"]));
    }

    [Fact]
    public async Task ResembleAI_uses_metadata_voice_fallback_and_omits_absent_optional_fields()
    {
        JsonElement payload = default;
        using var client = CreateClient(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                payload = ParseElement(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"audio_content":"AQ==","output_format":"wav"}""");
            }

            return JsonResponse("""{"items":[],"page":1,"num_pages":1}""");
        });
        var request = new SpeechRequest
        {
            Model = "chatterbox",
            Text = "hello",
            ProviderOptions = new()
            {
                ["resembleai"] = ParseElement("""{"voice_uuid":"metadata-voice","future_null":null}""")
            }
        };

        await CreateProvider(client).SpeechRequest(request);

        Assert.Equal("metadata-voice", payload.GetProperty("voice_uuid").GetString());
        Assert.False(payload.TryGetProperty("output_format", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("project_uuid", out _));
        Assert.False(payload.TryGetProperty("title", out _));
        Assert.False(payload.TryGetProperty("sample_rate", out _));
        Assert.False(payload.TryGetProperty("use_hd", out _));
        Assert.False(payload.TryGetProperty("apply_custom_pronunciations", out _));
    }

    [Fact]
    public async Task ResembleAI_preserves_validation_and_upstream_errors()
    {
        using var unusedClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var missingVoice = new SpeechRequest { Model = "chatterbox", Text = "hello" };
        var missingVoiceError = await Assert.ThrowsAsync<ArgumentException>(
            () => CreateProvider(unusedClient).SpeechRequest(missingVoice));
        Assert.Contains("requires a voice UUID", missingVoiceError.Message);

        var invalidOption = new SpeechRequest
        {
            Model = "chatterbox",
            Text = "hello",
            ProviderOptions = new()
            {
                ["resembleai"] = ParseElement("""{"voice_uuid":"voice","sample_rate":"fast"}""")
            }
        };
        await Assert.ThrowsAsync<JsonException>(() => CreateProvider(unusedClient).SpeechRequest(invalidOption));

        using var errorClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("invalid request")
        });
        var upstreamError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateProvider(errorClient).SpeechRequest(new SpeechRequest
            {
                Model = "chatterbox",
                Text = "hello",
                Voice = "voice"
            }));
        Assert.Equal("ResembleAI TTS failed (400): invalid request", upstreamError.Message);
    }

    private static void AssertUnknownFieldsForwarded(
        JsonElement source,
        JsonElement payload,
        params string[] replacedProperties)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (replacedProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase))
                continue;

            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
    }

    private static ResembleAIProvider CreateProvider(HttpClient client)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new DelegateHttpMessageHandler(responder));

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
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
