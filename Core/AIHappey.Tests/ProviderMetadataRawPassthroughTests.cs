using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.APIpie;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Core.Providers.EverypixelLabs;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderMetadataRawPassthroughTests
{
    [Fact]
    public async Task Apipie_image_options_merge_at_root_preserve_unknown_json_and_keep_request_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/images/generations", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""")
            };
        });
        var metadata = ParseElement("""
            {
              "quality": "high",
              "style": "vivid",
              "seed": 12,
              "steps": 23,
              "loras": ["one", "two"],
              "strength": 0.75,
              "aspect_ratio": "1:1",
              "text_layout": [{"text":"Hello","bbox":[1,2],"future_nested":{"ok":true}}],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt",
              "n": 99,
              "size": "metadata-size",
              "response_format": "url"
            }
            """);
        var request = new ImageRequest
        {
            Model = "apipie/image-model",
            Prompt = "A landscape",
            N = 1,
            Size = "512x768",
            Seed = 34,
            AspectRatio = "16:9",
            ProviderOptions = new()
            {
                ["apipie"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);

        await CreateApipieProvider(client).ImageRequest(request);

        Assert.Equal("apipie/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal("512x768", payload.GetProperty("size").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(34, payload.GetProperty("seed").GetInt32());
        Assert.Equal("16:9", payload.GetProperty("aspect_ratio").GetString());
        Assert.Equal("high", payload.GetProperty("quality").GetString());
        Assert.Equal("vivid", payload.GetProperty("style").GetString());
        Assert.Equal(23, payload.GetProperty("steps").GetInt32());
        Assert.Equal(2, payload.GetProperty("loras").GetArrayLength());
        Assert.Equal(0.75, payload.GetProperty("strength").GetDouble());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("text_layout"), payload.GetProperty("text_layout")));
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(1, payload.GetProperty("future_object").GetProperty("nested")[0].GetProperty("value").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("apipie", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Apipie_known_nulls_and_defaults_keep_existing_omission_and_validation()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"b64_json":"AQID"}]}""")
            };
        });
        var request = new ImageRequest
        {
            Model = "apipie/image-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["apipie"] = ParseElement(
                    """{"quality":null,"style":null,"seed":null,"steps":null,"loras":null,"strength":null,"aspect_ratio":null,"text_layout":null,"future_null":null}""")
            }
        };

        await CreateApipieProvider(client).ImageRequest(request);

        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(1, payload.GetProperty("n").GetInt32());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(4, payload.EnumerateObject().Count());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["apipie"] = ParseElement("""{"quality":42}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateApipieProvider(invalidClient).ImageRequest(request));
        request.ProviderOptions["apipie"] = ParseElement("""{"text_layout":[{"bbox":"invalid"}]}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateApipieProvider(invalidClient).ImageRequest(request));
    }

    [Fact]
    public async Task Deepinfra_reranking_options_remain_provider_keyed_and_forward_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/inference/rerank-model", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"scores":[0.75]}""")
            };
        });
        var request = CreateRerankingRequest(ParseElement("""
            {
              "instruction": "Compare relevance",
              "service_tier": "priority",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "queries": ["override"],
              "documents": ["override"]
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");
        var before = JsonSerializer.Serialize(request);

        await CreateDeepInfraProvider(client).RerankingRequest(request);

        Assert.Equal("Compare relevance", payload.GetProperty("instruction").GetString());
        Assert.Equal("priority", payload.GetProperty("service_tier").GetString());
        Assert.Equal("query", payload.GetProperty("queries")[0].GetString());
        Assert.Equal("document", payload.GetProperty("documents")[0].GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(request.ProviderOptions["deepinfra"].GetProperty(name),
                payload.GetProperty(name)), name);
        }

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("deepinfra", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "queries", "documents", "instruction", "service_tier",
                "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task Deepinfra_defaults_known_omissions_validation_and_errors_remain_compatible()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"scores":[0.75]}""")
            };
        });
        var request = CreateRerankingRequest(ParseElement(
            """{"instruction":"","service_tier":"","future_null":null}"""));

        await CreateDeepInfraProvider(client).RerankingRequest(request);

        Assert.False(payload.TryGetProperty("instruction", out _));
        Assert.False(payload.TryGetProperty("service_tier", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions!["deepinfra"] = ParseElement("""{"instruction":42}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateDeepInfraProvider(invalidClient).RerankingRequest(request));

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        request.ProviderOptions!["deepinfra"] = ParseElement("""{"future":true}""");
        var error = await Assert.ThrowsAsync<Exception>(() =>
            CreateDeepInfraProvider(failedClient).RerankingRequest(request));
        Assert.Equal("BadRequest: upstream failure", error.Message);
    }

    [Fact]
    public async Task EverypixelLabs_image_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/image_generate", message.RequestUri!.AbsolutePath);
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"task_id":"task-1"}""")
                };
            }

            if (message.RequestUri!.AbsolutePath == "/v1/status")
            {
                Assert.Equal("task-1", message.RequestUri.Query.Split('=').Last());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"SUCCESS","result":"https://api.everypixel.com/result.png"}""")
                };
            }

            Assert.Equal("/result.png", message.RequestUri.AbsolutePath);
            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "style": "realistic",
              "lora_url": "https://example.test/adapter",
              "model": "metadata-model",
              "prompt": "metadata prompt",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new ImageRequest
        {
            Model = "everypixellabs/image-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["everypixellabs"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var before = JsonSerializer.Serialize(request);
        var clientRequest = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        Assert.True(JsonElement.DeepEquals(
            metadata,
            clientRequest.GetProperty("providerOptions").GetProperty("everypixellabs")));
        Assert.True(clientRequest.GetProperty("providerOptions").TryGetProperty("another-provider", out _));

        await CreateEverypixelLabsProvider(client).ImageRequest(request);

        Assert.Equal("image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("realistic", payload.GetProperty("style").GetString());
        Assert.Equal("https://example.test/adapter", payload.GetProperty("lora_url").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("everypixellabs", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "model", "prompt", "style", "lora_url", "future_object", "future_array", "future_string",
                "future_number", "future_boolean", "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
        Assert.Equal(before, JsonSerializer.Serialize(request));
    }

    [Fact]
    public async Task EverypixelLabs_transcription_options_preserve_unknown_json_at_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/v1/transcribe", message.RequestUri!.AbsolutePath);
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"task_id":"task-1"}""")
                };
            }

            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"status":"SUCCESS","result":"transcribed"}""")
            };
        });
        var metadata = ParseElement("""
            {
              "language": "en",
              "hints": ["product", "name"],
              "audio_url": "metadata-audio",
              "future_object": {"nested": [{"value": true}]},
              "future_array": [1, null, "value"],
              "future_null": null
            }
            """);
        var request = new TranscriptionRequest
        {
            Model = "transcription-model",
            MediaType = "audio/wav",
            Audio = JsonSerializer.SerializeToElement("AQID"),
            ProviderOptions = new()
            {
                ["everypixellabs"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateEverypixelLabsProvider(client).TranscriptionRequest(request);

        Assert.Equal("data:audio/wav;base64,AQID", payload.GetProperty("audio_url").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.Equal("product", payload.GetProperty("hints")[0].GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), payload.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), payload.GetProperty("future_array")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("everypixellabs", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[] { "audio_url", "language", "hints", "future_object", "future_array", "future_null" }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task EverypixelLabs_video_options_preserve_unknown_json_and_existing_override_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/video_generate", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return new(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"task_id":"task-1","status":"PENDING"}""")
            };
        });
        var metadata = ParseElement("""
            {
              "resolution": "metadata-resolution",
              "model": "metadata-model",
              "prompt": "metadata prompt",
              "duration": 99,
              "future_object": {"nested": [{"value": true}]},
              "future_array": [1, null, "value"],
              "future_boolean": false,
              "future_null": null
            }
            """);
        var request = new VideoRequest
        {
            Model = "video-model",
            Prompt = "A landscape",
            Duration = 5,
            Resolution = "request-resolution",
            ProviderOptions = new()
            {
                ["everypixellabs"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateEverypixelLabsProvider(client).StartVideoOperation(request);

        Assert.Equal("video-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal(5, payload.GetProperty("duration").GetInt32());
        Assert.Equal("metadata-resolution", payload.GetProperty("resolution").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_object"), payload.GetProperty("future_object")));
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_array"), payload.GetProperty("future_array")));
        Assert.False(payload.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("everypixellabs", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "model", "prompt", "duration", "resolution", "future_object", "future_array", "future_boolean",
                "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task EverypixelLabs_speech_options_keep_known_mapping_and_validate_known_types()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"task_id":"task-1"}""")
                };
            }

            if (message.RequestUri!.AbsolutePath == "/v1/status")
            {
                return new(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"status":"SUCCESS","result":"https://api.everypixel.com/audio.wav"}""")
                };
            }

            return new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "style": "  warm  ",
              "prompt": "  provider prompt  ",
              "seed": 42,
              "callback_url": "  https://example.test/callback  ",
              "text": "metadata text",
              "future_nested": {"items": [null, 1, {"enabled": true}]},
              "future_null": null
            }
            """);
        var request = new SpeechRequest
        {
            Model = "everypixellabs/tts_create",
            Text = "Hello",
            Voice = "speaker-1",
            Instructions = "request instructions",
            Language = "en",
            ProviderOptions = new()
            {
                ["everypixellabs"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateEverypixelLabsProvider(client).SpeechRequest(request);

        Assert.Equal("Hello", payload.GetProperty("text").GetString());
        Assert.Equal("speaker-1", payload.GetProperty("speaker").GetString());
        Assert.Equal("warm", payload.GetProperty("style").GetString());
        Assert.Equal("provider prompt", payload.GetProperty("prompt").GetString());
        Assert.Equal(42, payload.GetProperty("seed").GetInt32());
        Assert.Equal("https://example.test/callback", payload.GetProperty("callback_url").GetString());
        Assert.Equal("en", payload.GetProperty("language").GetString());
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("future_nested"), payload.GetProperty("future_nested")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("everypixellabs", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.Equal(
            new[]
            {
                "text", "speaker", "style", "language", "prompt", "seed", "callback_url", "future_nested",
                "future_null"
            }.Order(),
            payload.EnumerateObject().Select(property => property.Name).Order());

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions!["everypixellabs"] = ParseElement("""{"seed":"invalid"}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateEverypixelLabsProvider(invalidClient).SpeechRequest(request));

        request.ProviderOptions!["everypixellabs"] =
            ParseElement("""{"style":null,"prompt":"","seed":null,"callback_url":null,"future_null":null}""");
        payload = default;
        await CreateEverypixelLabsProvider(client).SpeechRequest(request);
        Assert.False(payload.TryGetProperty("style", out _));
        Assert.Equal("request instructions", payload.GetProperty("prompt").GetString());
        Assert.False(payload.TryGetProperty("seed", out _));
        Assert.False(payload.TryGetProperty("callback_url", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);

        using var failedClient = CreateClient(_ => new(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateEverypixelLabsProvider(failedClient).SpeechRequest(request));
        Assert.Equal("EverypixelLabs tts create failed (400): upstream failure", error.Message);
    }

    private static RerankingRequest CreateRerankingRequest(JsonElement options)
        => new()
        {
            Model = "rerank-model",
            Query = "query",
            Documents = new()
            {
                Type = "text",
                Values = ParseElement("""["document"]""")
            },
            ProviderOptions = new()
            {
                ["deepinfra"] = options
            }
        };

    private static APIpieProvider CreateApipieProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static DeepInfraProvider CreateDeepInfraProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static EverypixelLabsProvider CreateEverypixelLabsProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

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
