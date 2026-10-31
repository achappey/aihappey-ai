using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.ARKLabs;
using AIHappey.Core.Providers.Fireworks;
using AIHappey.Core.Providers.Jina;
using AIHappey.Core.Providers.Pinecone;
using AIHappey.Core.Providers.Portkey;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Core.Providers.VoyageAI;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Pollinations_unknown_metadata_is_merged_at_the_request_root()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return JsonResponse("""{"data":[{"id":"openai"}]}""");

            Assert.Equal("/openai", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });
        var provider = new PollinationsProvider(new KeyResolver(), new HttpContextAccessor(), new ClientFactory(client));
        var request = new ChatRequest
        {
            Id = "chat-1",
            Model = "pollinations/openai",
            Messages =
            [
                new UIMessage
                {
                    Id = "user-1",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "Hello" }]
                }
            ],
            ProviderMetadata = new()
            {
                ["pollinations"] = ParseElement("""
                    {
                      "reasoning_effort": "high",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await foreach (var _ in provider.StreamAsync(request))
        {
        }

        var options = request.ProviderMetadata["pollinations"];
        foreach (var property in options.EnumerateObject())
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        Assert.Equal("pollinations/openai", payload.GetProperty("model").GetString());
        Assert.Equal("high", payload.GetProperty("reasoning_effort").GetString());
        Assert.Equal(JsonValueKind.Array, payload.GetProperty("messages").ValueKind);
        Assert.False(payload.TryGetProperty("providerMetadata", out _));
        Assert.False(payload.TryGetProperty("pollinations", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.False(request.ProviderMetadata["pollinations"].TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Pollinations_known_metadata_validation_is_preserved()
    {
        using var client = CreateClient(message => message.Method == HttpMethod.Get
            ? JsonResponse("""{"data":[{"id":"openai"}]}""")
            : throw new InvalidOperationException("Request should not be sent."));
        var provider = new PollinationsProvider(new KeyResolver(), new HttpContextAccessor(), new ClientFactory(client));
        var request = new ChatRequest
        {
            Model = "pollinations/openai",
            Messages = [],
            ProviderMetadata = new()
            {
                ["pollinations"] = ParseElement("""{"reasoning_effort":false}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(async () =>
        {
            await foreach (var _ in provider.StreamAsync(request))
            {
            }
        });
    }

    [Fact]
    public async Task Jina_provider_options_keep_known_fields_and_forward_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"results":[{"index":0,"relevance_score":0.9}],"usage":{"total_tokens":1}}""");
        });
        var request = CreateRerankingRequest("jina", """
            {
              "return_documents": false,
              "return_embeddings": null,
              "truncation": true,
              "max_doc_length": 21,
              "model": "provider-model",
              "query": "provider-query",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");

        var response = await new JinaProvider(new KeyResolver(), new ClientFactory(client))
            .RerankingRequest(request);

        foreach (var property in request.ProviderOptions["jina"].EnumerateObject())
        {
            if (property.Name is "return_embeddings" or "model" or "query")
                continue;
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.False(payload.TryGetProperty("return_embeddings", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("jina", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.Contains("jina", response.ProviderMetadata!.Keys);
        Assert.DoesNotContain("another-provider", response.ProviderMetadata.Keys);
        Assert.Equal("false", JsonSerializer.Serialize(request.ProviderOptions["jina"].GetProperty("return_documents")));
    }

    [Fact]
    public async Task Pinecone_provider_options_keep_known_fields_and_forward_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[{"index":0,"score":0.9}]}""");
        });
        var request = CreateRerankingRequest("pinecone", """
            {
              "return_documents": false,
              "rank_fields": ["title", "body"],
              "parameters": {"nested": [{"value": 1}, [true, null, "text"]]},
              "model": "provider-model",
              "query": "provider-query",
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");

        var cache = new MemoryCache(new MemoryCacheOptions());
        var provider = new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client));
        await provider.RerankingRequest(request);

        foreach (var property in request.ProviderOptions["pinecone"].EnumerateObject())
        {
            if (property.Name is "model" or "query")
                continue;
            Assert.True(JsonElement.DeepEquals(property.Value, payload.GetProperty(property.Name)), property.Name);
        }
        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("pinecone", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
    }

    [Fact]
    public async Task Pinecone_null_and_empty_known_options_remain_omitted()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[]}""");
        });
        var request = CreateRerankingRequest("pinecone", """
            {"return_documents":null,"rank_fields":[],"parameters":null}
            """);
        var cache = new MemoryCache(new MemoryCacheOptions());

        await new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client))
            .RerankingRequest(request);

        Assert.False(payload.TryGetProperty("return_documents", out _));
        Assert.False(payload.TryGetProperty("rank_fields", out _));
        Assert.False(payload.TryGetProperty("parameters", out _));
        Assert.Equal(
            new[] { "documents", "model", "query", "top_n" },
            payload.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Fact]
    public async Task VoyageAI_provider_options_forward_unknown_json_without_changing_provider_namespace()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[{"index":0,"relevance_score":0.9}]}""");
        });
        var request = CreateRerankingRequest("voyageai", """
            {
              "return_documents": false,
              "truncation": true,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "provider-model",
              "query": "provider-query"
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");
        var originalOptions = request.ProviderOptions["voyageai"].Clone();

        await new VoyageAIProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request);

        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["voyageai"]));
        Assert.Equal("false", JsonSerializer.Serialize(payload.GetProperty("return_documents")));
        Assert.True(payload.GetProperty("truncation").GetBoolean());
        foreach (var name in new[]
        {
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
        })
        {
            Assert.True(JsonElement.DeepEquals(originalOptions.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.True(JsonElement.DeepEquals(request.Documents.Values, payload.GetProperty("documents")));
        Assert.Equal(1, payload.GetProperty("top_k").GetInt32());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("voyageai", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Fireworks_provider_options_forward_unknown_json_without_changing_provider_namespace()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/rerank", message.RequestUri!.AbsolutePath);
            payload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[{"index":0,"relevance_score":0.9}]}""");
        });
        var request = CreateRerankingRequest("fireworks", """
            {
              "task": "rank relevant passages",
              "return_documents": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "provider-model",
              "query": "provider-query"
            }
            """);
        request.ProviderOptions!["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""");
        var originalOptions = request.ProviderOptions["fireworks"].Clone();
        var cache = new MemoryCache(new MemoryCacheOptions());

        await new FireworksProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client))
            .RerankingRequest(request);

        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["fireworks"]));
        Assert.Equal("rank relevant passages", payload.GetProperty("task").GetString());
        Assert.Equal("false", JsonSerializer.Serialize(payload.GetProperty("return_documents")));
        foreach (var name in new[]
        {
            "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
        })
        {
            Assert.True(JsonElement.DeepEquals(originalOptions.GetProperty(name), payload.GetProperty(name)), name);
        }

        Assert.Equal(request.Model, payload.GetProperty("model").GetString());
        Assert.Equal(request.Query, payload.GetProperty("query").GetString());
        Assert.Equal(1, payload.GetProperty("top_n").GetInt32());
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("fireworks", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task VoyageAI_and_Fireworks_null_known_options_remain_omitted()
    {
        JsonElement voyagePayload = default;
        using var voyageClient = CreateClient(message =>
        {
            voyagePayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[]}""");
        });
        var voyageRequest = CreateRerankingRequest("voyageai", """{"return_documents":null,"truncation":null}""");

        await new VoyageAIProvider(new KeyResolver(), new ClientFactory(voyageClient)).RerankingRequest(voyageRequest);

        Assert.False(voyagePayload.TryGetProperty("return_documents", out _));
        Assert.False(voyagePayload.TryGetProperty("truncation", out _));
        Assert.Equal(
            new[] { "documents", "model", "query", "top_k" },
            voyagePayload.EnumerateObject().Select(property => property.Name).Order().ToArray());

        JsonElement fireworksPayload = default;
        using var fireworksClient = CreateClient(message =>
        {
            fireworksPayload = JsonDocument.Parse(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult()).RootElement.Clone();
            return JsonResponse("""{"data":[]}""");
        });
        var fireworksRequest = CreateRerankingRequest("fireworks", """{"task":" ","return_documents":null}""");
        var cache = new MemoryCache(new MemoryCacheOptions());

        await new FireworksProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(fireworksClient))
            .RerankingRequest(fireworksRequest);

        Assert.False(fireworksPayload.TryGetProperty("task", out _));
        Assert.False(fireworksPayload.TryGetProperty("return_documents", out _));
        Assert.Equal(
            new[] { "documents", "model", "query", "top_n" },
            fireworksPayload.EnumerateObject().Select(property => property.Name).Order().ToArray());
    }

    [Theory]
    [InlineData("jina", """{"return_documents":"false"}""")]
    [InlineData("pinecone", """{"rank_fields":[1]}""")]
    [InlineData("voyageai", """{"return_documents":"false"}""")]
    [InlineData("fireworks", """{"task":1}""")]
    public async Task Invalid_known_provider_options_still_fail_before_sending(string providerId, string options)
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = CreateRerankingRequest(providerId, options);

        if (providerId == "jina")
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                new JinaProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request));
            return;
        }

        if (providerId == "voyageai")
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                new VoyageAIProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request));
            return;
        }

        var cache = new MemoryCache(new MemoryCacheOptions());
        if (providerId == "fireworks")
        {
            await Assert.ThrowsAsync<JsonException>(() =>
                new FireworksProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client))
                    .RerankingRequest(request));
            return;
        }

        await Assert.ThrowsAsync<JsonException>(() =>
            new PineconeProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(client)).RerankingRequest(request));
    }

    [Fact]
    public async Task VoyageAI_and_Fireworks_error_responses_remain_provider_specific()
    {
        using var voyageClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var voyageError = await Assert.ThrowsAsync<Exception>(() =>
            new VoyageAIProvider(new KeyResolver(), new ClientFactory(voyageClient))
                .RerankingRequest(CreateRerankingRequest("voyageai", """{"future_option":true}""")));
        Assert.Equal("upstream error", voyageError.Message);

        using var fireworksClient = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var cache = new MemoryCache(new MemoryCacheOptions());
        var fireworksError = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FireworksProvider(new KeyResolver(), new AsyncCacheHelper(cache), new ClientFactory(fireworksClient))
                .RerankingRequest(CreateRerankingRequest("fireworks", """{"future_option":true}""")));
        Assert.Equal("Fireworks rerank failed (400): upstream error", fireworksError.Message);
    }

    [Fact]
    public async Task Jina_error_response_body_remains_the_thrown_message()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var request = CreateRerankingRequest("jina", """{"future_option":true}""");

        var error = await Assert.ThrowsAsync<Exception>(() =>
            new JinaProvider(new KeyResolver(), new ClientFactory(client)).RerankingRequest(request));

        Assert.Equal("upstream error", error.Message);
    }

    [Fact]
    public async Task Portkey_transcription_options_map_known_fields_and_forward_unknown_multipart_values()
    {
        Dictionary<string, string[]> payload = [];
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/audio/transcriptions", message.RequestUri!.AbsolutePath);
            payload = ReadMultipart(message);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var request = new TranscriptionRequest
        {
            Model = "portkey/whisper",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["portkey"] = ParseElement("""
                    {
                      "language": "en",
                      "prompt": "dictation",
                      "response_format": "JSON",
                      "temperature": 0.25,
                      "timestamp_granularities": ["word", "segment"],
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """),
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var originalOptions = request.ProviderOptions["portkey"].Clone();
        var provider = new PortkeyProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(client));

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("portkey/whisper", payload["model"].Single());
        Assert.Equal("en", payload["language"].Single());
        Assert.Equal("dictation", payload["prompt"].Single());
        Assert.Equal("json", payload["response_format"].Single());
        Assert.Equal("0.25", payload["temperature"].Single());
        Assert.Equal(new[] { "word", "segment" }, payload["timestamp_granularities[]"]);
        Assert.Equal("unchanged", payload["future_string"].Single());
        Assert.Equal("12345678901234567890.123456789", payload["future_number"].Single());
        Assert.Equal("false", payload["future_boolean"].Single());
        Assert.Equal("null", payload["future_null"].Single());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"nested":[{"value":1},[true,null,"text"]]}"""),
            ParseElement(payload["future_object"].Single())));
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""[1,{"more":[false,2.5]},[]]"""),
            ParseElement(payload["future_array"].Single())));
        Assert.Equal(
            new[]
            {
                "file", "future_array", "future_boolean", "future_null", "future_number", "future_object",
                "future_string", "language", "model", "prompt", "response_format", "temperature",
                "timestamp_granularities[]"
            },
            payload.Keys.Order().ToArray());
        Assert.False(payload.ContainsKey("providerOptions"));
        Assert.False(payload.ContainsKey("another-provider"));
        Assert.False(payload.ContainsKey("secret"));
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["portkey"]));
        Assert.True(request.ProviderOptions.ContainsKey("another-provider"));
    }

    [Fact]
    public async Task Portkey_null_and_empty_known_options_remain_omitted()
    {
        Dictionary<string, string[]> payload = [];
        using var client = CreateClient(message =>
        {
            payload = ReadMultipart(message);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var provider = new PortkeyProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(client));
        await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "portkey/whisper",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["portkey"] = ParseElement("""
                    {
                      "language": null,
                      "prompt": " ",
                      "response_format": null,
                      "temperature": null,
                      "timestamp_granularities": [],
                      "future_null": null
                    }
                    """)
            }
        });

        Assert.Equal(new[] { "file", "future_null", "model" }, payload.Keys.Order().ToArray());
        Assert.Equal("null", payload["future_null"].Single());
    }

    [Fact]
    public async Task ARKLabs_transcription_options_keep_defaults_and_forward_unknown_multipart_values()
    {
        Dictionary<string, string[]> payload = [];
        using var client = CreateClient(message =>
        {
            Assert.Equal("/api/v1/audio/transcriptions", message.RequestUri!.AbsolutePath);
            payload = ReadMultipart(message);
            return JsonResponse("""{"text":"recognized"}""");
        });
        var request = new TranscriptionRequest
        {
            Model = "arklabs/whisper",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["arklabs"] = ParseElement("""
                    {
                      "language": "en",
                      "prompt": "dictation",
                      "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
                      "future_array": [1, {"more": [false, 2.5]}, []],
                      "future_string": "unchanged",
                      "future_number": 12345678901234567890.123456789,
                      "future_boolean": false,
                      "future_null": null
                    }
                    """)
            }
        };
        var originalOptions = request.ProviderOptions["arklabs"].Clone();
        var provider = new ARKLabsProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(client));

        var response = await provider.TranscriptionRequest(request);

        Assert.Equal("recognized", response.Text);
        Assert.Equal("arklabs/whisper", payload["model"].Single());
        Assert.Equal("en", payload["language"].Single());
        Assert.Equal("dictation", payload["prompt"].Single());
        Assert.Equal("json", payload["response_format"].Single());
        Assert.Equal("unchanged", payload["future_string"].Single());
        Assert.Equal("12345678901234567890.123456789", payload["future_number"].Single());
        Assert.Equal("false", payload["future_boolean"].Single());
        Assert.Equal("null", payload["future_null"].Single());
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""{"nested":[{"value":1},[true,null,"text"]]}"""),
            ParseElement(payload["future_object"].Single())));
        Assert.True(JsonElement.DeepEquals(
            ParseElement("""[1,{"more":[false,2.5]},[]]"""),
            ParseElement(payload["future_array"].Single())));
        Assert.Equal(
            new[]
            {
                "file", "future_array", "future_boolean", "future_null", "future_number", "future_object",
                "future_string", "language", "model", "prompt", "response_format"
            },
            payload.Keys.Order().ToArray());
        Assert.False(payload.ContainsKey("providerOptions"));
        Assert.True(JsonElement.DeepEquals(originalOptions, request.ProviderOptions["arklabs"]));
    }

    [Fact]
    public async Task ARKLabs_srt_response_format_and_invalid_known_options_keep_existing_behavior()
    {
        Dictionary<string, string[]> payload = [];
        using var client = CreateClient(message =>
        {
            payload = ReadMultipart(message);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("1\n00:00:00,000 --> 00:00:01,000\nrecognized")
            };
        });
        var provider = new ARKLabsProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(client));
        var response = await provider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "arklabs/whisper",
            Audio = "AQID",
            ProviderOptions = new()
            {
                ["arklabs"] = ParseElement("""{"response_format":"srt"}""")
            }
        });

        Assert.Equal("srt", payload["response_format"].Single());
        Assert.Equal("recognized", response.Segments.Single().Text);

        using var noSendClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var invalidProvider = new ARKLabsProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(noSendClient));
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "arklabs/whisper",
            Audio = "AQID",
            ProviderOptions = new()
            {
                ["arklabs"] = ParseElement("""{"language":false}""")
            }
        }));
        var invalidPortkeyProvider = new PortkeyProvider(
            new KeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(noSendClient));
        await Assert.ThrowsAsync<JsonException>(() => invalidPortkeyProvider.TranscriptionRequest(new TranscriptionRequest
        {
            Model = "portkey/whisper",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
            {
                ["portkey"] = ParseElement("""{"language":false}""")
            }
        }));
    }

    [Theory]
    [InlineData("portkey", "Portkey STT failed (400): upstream error")]
    [InlineData("arklabs", "ARKLabs transcription failed (400): upstream error")]
    public async Task Transcription_upstream_errors_keep_provider_specific_messages(string providerId, string expectedMessage)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var request = new TranscriptionRequest
        {
            Model = $"{providerId}/whisper",
            Audio = "AQID",
            MediaType = "audio/wav",
            ProviderOptions = new()
        };
        Task<TranscriptionResponse> Transcribe()
            => providerId == "portkey"
                ? new PortkeyProvider(
                    new KeyResolver(),
                    new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
                    new ClientFactory(client)).TranscriptionRequest(request)
                : new ARKLabsProvider(
                    new KeyResolver(),
                    new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
                    new ClientFactory(client)).TranscriptionRequest(request);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(Transcribe);

        Assert.Equal(expectedMessage, error.Message);
    }

    private static RerankingRequest CreateRerankingRequest(string providerId, string options)
        => new()
        {
            Model = providerId switch
            {
                "jina" => "jina/jina-reranker-v3",
                "voyageai" => "voyageai/rerank-2",
                "fireworks" => "fireworks/rerank-v1",
                _ => "pinecone/rerank-v0"
            },
            Query = "query",
            TopN = 1,
            Documents = new()
            {
                Type = "text",
                Values = ParseElement("""["first","second"]""")
            },
            ProviderOptions = new()
            {
                [providerId] = ParseElement(options)
            }
        };

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static Dictionary<string, string[]> ReadMultipart(HttpRequestMessage message)
        => ((MultipartFormDataContent)message.Content!)
            .GroupBy(part => part.Headers.ContentDisposition!.Name!.Trim('"'))
            .ToDictionary(
                group => group.Key,
                group => group.Select(part => part.ReadAsStringAsync().GetAwaiter().GetResult()).ToArray());

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new Handler(send));

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(send(request));
    }
}
