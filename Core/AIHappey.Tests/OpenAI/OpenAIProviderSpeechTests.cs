using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.OpenAI;

public sealed class OpenAIProviderSpeechTests
{
    public static IEnumerable<object[]> RealtimeSpeechModels =>
        new[] { "gpt-realtime-2.1", "gpt-realtime-2.1-mini", "gpt-realtime-2", "gpt-realtime-1.5" }
            .SelectMany(id => new[] { new object[] { id }, new object[] { id + "-2026-10-01" } });

    [Fact]
    public async Task DiscoveryHidesOnlyRequestedLegacySpeechModelsAndPreservesRealtimeCapabilities()
    {
        string[] excluded = ["tts-1", "tts-1-hd", "gpt-4o-mini-tts", "gpt-4o-mini-tts-2025-03-20", "gpt-4o-mini-tts-2025-12-15"];
        var speechModels = RealtimeSpeechModels.Select(row => (string)row[0]).ToArray();
        string[] unsupported = ["gpt-realtime-2-mini", "gpt-realtime-2.10", "gpt-realtime-1.5-mini", "gpt-realtime-2.1-unexpected", "gpt-realtime-2.1-2026-10", "gpt-realtime-2.1-mini-2026-10-01-extra", "gpt-realtime", "gpt-4o-realtime-preview", "gpt-4o-mini-tts-other", "gpt-live-1"];
        var retained = speechModels.Concat(unsupported).ToArray();
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? JsonResponse(JsonSerializer.Serialize(new { data = excluded.Concat(retained).Select(id => new { id, owned_by = "openai" }) }))
            : JsonResponse("""{"data":[],"has_more":false}"""));
        var models = (await provider.ListModels()).ToArray();
        Assert.DoesNotContain(models, model => excluded.Contains(model.Name));
        foreach (var id in retained) Assert.Contains(models, model => model.Id == "openai/" + id);
        foreach (var id in speechModels)
        {
            var speech = Assert.Single(models, model => model.Id == "openai/" + id);
            Assert.Equal("audio", speech.Type);
            foreach (var tag in new[] { "real-time", "realtime-token", "realtime-session", "realtime-speech" }) Assert.Contains(tag, speech.Tags!);
        }
        foreach (var id in unsupported)
            Assert.DoesNotContain("realtime-speech", Assert.Single(models, model => model.Id == "openai/" + id).Tags ?? []);
        Assert.DoesNotContain(models, model => model.Type == "speech" && model.Tags?.Contains("realtime-speech") == true);
    }

    [Fact]
    public async Task RealtimeSpeechDiscoveryDoesNotFabricateModelsOrRestoreShutdownModels()
    {
        var provider = CreateProvider(request => request.RequestUri!.AbsolutePath == "/v1/models"
            ? JsonResponse("""{"data":[{"id":"gpt-realtime-2.1-mini"},{"id":"gpt-realtime-2.1","shutdown_date":"2026-10-01"}]}""")
            : JsonResponse("""{"data":[],"has_more":false}"""));
        var models = (await provider.ListModels()).ToArray();
        Assert.Contains(models, model => model.Id == "openai/gpt-realtime-2.1-mini" && model.Tags!.Contains("realtime-speech"));
        foreach (var id in new[] { "gpt-realtime-2.1", "gpt-realtime-2", "gpt-realtime-1.5" })
            Assert.DoesNotContain(models, model => model.Id == "openai/" + id);
    }

    [Theory]
    [MemberData(nameof(RealtimeSpeechModels))]
    public async Task AgentSpeechCredentialsUseGaOutputOnlySessionAndReturnOnlyEphemeralCredentials(string model)
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider(request =>
        {
            captured = CloneRequest(request);
            return JsonResponse("""{"value":"ek_fixture","expires_at":2000000000,"session":{"type":"realtime"}}""");
        });
        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = model,
            ProviderOptions = new() { ["openai"] = JsonSerializer.SerializeToElement(new
            {
                session = new { type = "realtime", model = "caller-cannot-override", output_modalities = new[] { "audio" }, tools = Array.Empty<object>(),
                    audio = new { input = new { turn_detection = (object?)null }, output = new { voice = "marin", format = new { type = "audio/pcm", rate = 24000 } } } }
            }) }
        });
        Assert.Equal("/v1/realtime/client_secrets", captured!.RequestUri!.AbsolutePath);
        Assert.False(captured.Headers.Contains("OpenAI-Beta"));
        var payload = JsonDocument.Parse(await captured.Content!.ReadAsStringAsync()).RootElement.GetProperty("session");
        Assert.Equal(model, payload.GetProperty("model").GetString());
        Assert.Equal("audio", payload.GetProperty("output_modalities")[0].GetString());
        Assert.Equal(24000, payload.GetProperty("audio").GetProperty("output").GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("audio").GetProperty("input").GetProperty("turn_detection").ValueKind);
        Assert.Empty(payload.GetProperty("tools").EnumerateArray());
        Assert.Equal("ek_fixture", response.Value); Assert.Equal(2000000000, response.ExpiresAt);
        Assert.DoesNotContain("test-key", JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task SpeechRequestPostsRestPayloadAndReturnsBinaryAudioWithMetadataCost()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedRequest = CloneRequest(request);
                return BinaryResponse([1, 2, 3], "audio/wav");
            }

            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "id": "tts-1",
                          "object": "model",
                          "created": 1710000000,
                          "owned_by": "openai"
                        }
                      ]
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "hello",
            Voice = "nova",
            OutputFormat = "wav",
            Speed = 1.25f,
            Instructions = "speak warmly"
        });

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal("/v1/audio/speech", capturedRequest.RequestUri?.AbsolutePath);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization?.Scheme);
        Assert.Equal("test-key", capturedRequest.Headers.Authorization?.Parameter);

        var body = await capturedRequest.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal("tts-1", root.GetProperty("model").GetString());
        Assert.Equal("hello", root.GetProperty("input").GetString());
        Assert.Equal("nova", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1.25, root.GetProperty("speed").GetDouble(), 3);
        Assert.Equal("speak warmly", root.GetProperty("instructions").GetString());

        Assert.Equal(Convert.ToBase64String([1, 2, 3]), response.Audio.Base64);
        Assert.Equal("audio/wav", response.Audio.MimeType);
        Assert.Equal("wav", response.Audio.Format);
        Assert.NotNull(response.Request?.Body);

    }

    [Fact]
    public async Task SpeechRequestUsesProviderOptionsAndFallsBackMimeTypeWhenResponseOmitsContentType()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedRequest = CloneRequest(request);
                return BinaryResponse([4, 5, 6], contentType: null);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/models")
            {
                return JsonResponse("""
                    {
                      "data": [
                        {
                          "id": "tts-1",
                          "object": "model",
                          "created": 1710000000,
                          "owned_by": "openai"
                        }
                      ]
                    }
                    """);
            }

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        var providerOptions = ProviderOptions("""
            {
              "voice": "sage",
              "response_format": "pcm",
              "speed": 0.75,
              "instructions": "calm",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "provider-model",
              "input": "provider-input"
            }
            """);
        providerOptions["another-provider"] = JsonDocument.Parse("""{"secret":"not-forwarded"}""").RootElement.Clone();
        var originalOpenAiOptions = providerOptions["openai"].Clone();
        var response = await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "metadata driven",
            ProviderOptions = providerOptions
        });

        var body = await capturedRequest!.Content!.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var originalOptions = JsonDocument.Parse("""
            {
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """).RootElement;
        Assert.Equal("sage", root.GetProperty("voice").GetString());
        Assert.Equal("pcm", root.GetProperty("response_format").GetString());
        Assert.Equal(0.75, root.GetProperty("speed").GetDouble(), 3);
        Assert.Equal("calm", root.GetProperty("instructions").GetString());
        foreach (var name in new[]
        {
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
        })
        {
            Assert.True(JsonElement.DeepEquals(originalOptions.GetProperty(name), root.GetProperty(name)), name);
        }
        Assert.Equal("tts-1", root.GetProperty("model").GetString());
        Assert.Equal("metadata driven", root.GetProperty("input").GetString());
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("openai", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
        Assert.False(root.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(originalOpenAiOptions, providerOptions["openai"]));
        Assert.True(providerOptions.ContainsKey("another-provider"));
        Assert.Equal("audio/pcm", response.Audio.MimeType);
        Assert.Equal("pcm", response.Audio.Format);
    }

    [Fact]
    public async Task SpeechRequestFields_override_provider_options_and_keep_defaults()
    {
        HttpRequestMessage? capturedRequest = null;
        var provider = CreateProvider(request =>
        {
            if (request.RequestUri?.AbsolutePath == "/v1/audio/speech")
            {
                capturedRequest = CloneRequest(request);
                return BinaryResponse([4, 5, 6], contentType: null);
            }

            if (request.RequestUri?.AbsolutePath == "/v1/models")
                return JsonResponse("""{"data":[]}""");

            throw new InvalidOperationException($"Unexpected request path: {request.RequestUri}");
        });

        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "requested text",
            Voice = "request voice",
            OutputFormat = "wav",
            Speed = 1.25f,
            Instructions = "request instructions",
            ProviderOptions = ProviderOptions("""
                {
                  "voice": "metadata voice",
                  "response_format": "opus",
                  "speed": 0.5,
                  "instructions": "metadata instructions"
                }
                """)
        });

        using var document = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("request voice", root.GetProperty("voice").GetString());
        Assert.Equal("wav", root.GetProperty("response_format").GetString());
        Assert.Equal(1.25, root.GetProperty("speed").GetDouble(), 3);
        Assert.Equal("request instructions", root.GetProperty("instructions").GetString());

        capturedRequest = null;
        await provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "defaulted",
            ProviderOptions = ProviderOptions("""{"voice":null,"response_format":null,"speed":null,"instructions":null}""")
        });

        using var defaultDocument = JsonDocument.Parse(await capturedRequest!.Content!.ReadAsStringAsync());
        var defaultRoot = defaultDocument.RootElement;
        Assert.Equal("alloy", defaultRoot.GetProperty("voice").GetString());
        Assert.Equal("mp3", defaultRoot.GetProperty("response_format").GetString());
        Assert.Equal(1f, defaultRoot.GetProperty("speed").GetSingle());
        Assert.False(defaultRoot.TryGetProperty("instructions", out _));
    }

    [Theory]
    [InlineData("""{"response_format":1}""")]
    [InlineData("""{"speed":"fast"}""")]
    [InlineData("""{"instructions":false}""")]
    public async Task SpeechRequest_invalid_known_provider_options_still_fail_before_sending(string options)
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<JsonException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "invalid metadata",
            ProviderOptions = ProviderOptions(options)
        }));
    }

    [Fact]
    public async Task SpeechRequestThrowsOpenAiErrorBodyOnFailure()
    {
        var provider = CreateProvider(request =>
        {
            Assert.Equal("/v1/audio/speech", request.RequestUri?.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("{\"error\":{\"message\":\"bad input\"}}", Encoding.UTF8, "application/json")
            };
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.SpeechRequest(new SpeechRequest
        {
            Model = "tts-1",
            Text = "bad input"
        }));

        Assert.Contains("OpenAI speech request failed (400)", ex.Message);
        Assert.Contains("bad input", ex.Message);
    }

    private static OpenAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new StaticResponseHttpMessageHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new NullEndUserIdResolver());

    private static Dictionary<string, JsonElement> ProviderOptions(string metadata)
        => new()
        {
            ["openai"] = JsonDocument.Parse(metadata).RootElement.Clone()
        };

    private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);

        if (request.Headers.Authorization is not null)
            clone.Headers.Authorization = request.Headers.Authorization;

        if (request.Content is not null)
        {
            var content = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            clone.Content = new StringContent(content, Encoding.UTF8, request.Content.Headers.ContentType?.MediaType ?? "application/json");
        }

        return clone;
    }

    private static HttpResponseMessage BinaryResponse(byte[] body, string? contentType)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(body)
        };

        if (!string.IsNullOrWhiteSpace(contentType))
            response.Content.Headers.ContentType = new(contentType);

        return response;
    }

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class NullEndUserIdResolver : IEndUserIdResolver
    {
        public string? Resolve(ChatRequest chatRequest) => null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
