using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SenseNova;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.SenseNova;

public sealed class SenseNovaProviderTests
{
    private const string Completion = """
        {"id":"chat-1","object":"chat.completion","created":1,"model":"vendor/chat",
         "choices":[{"index":0,"message":{"role":"assistant","content":"Hello","reasoning":"Thinking"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}
        """;

    [Fact]
    public async Task Discovery_preserves_native_ids_and_model_types_and_caches_per_key()
    {
        var calls = 0;
        var key = new KeyResolver("first-key");
        var provider = CreateProvider((request, _) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://token.sensenova.cn/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal(key.Key, request.Headers.Authorization.Parameter);
            return Task.FromResult(JsonResponse("""
                {"object":"list","data":[
                  {"id":"vendor/chat","object":"model","created":123,"owned_by":"vendor","name":"Vision Chat",
                   "input_modalities":["text","image"],"output_modalities":["text"],"extra":{"limit":42}},
                  {"id":"image-model","input_modalities":["text","image"],"output_modalities":["image"]},
                  {"id":"image-model","input_modalities":["text"],"output_modalities":["image"]},
                  {"id":"audio-model","input_modalities":["audio"],"output_modalities":["audio"]},
                  {"id":"missing-modalities"},
                  {"id":"","input_modalities":["text"],"output_modalities":["text"]},
                  "malformed"
                ]}
                """));
        }, key);

        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(2, models.Length);
        Assert.Equal("sensenova/vendor/chat", models[0].Id);
        Assert.Equal("Vision Chat", models[0].Name);
        Assert.Equal("vendor", models[0].OwnedBy);
        Assert.Equal(123, models[0].Created);
        Assert.Equal("language", models[0].Type);
        Assert.Equal("image", models[1].Type);
        Assert.Equal("sensenova/image-model", models[1].Id);
        Assert.Null(models[1].Tags);
        Assert.Equal(2, (await provider.ListModels()).Count());
        Assert.Equal(1, calls);
        key.Key = "second-key";
        await provider.ListModels();
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task Missing_key_skips_discovery_and_rejects_inference(string? key)
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network."), new(key));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CompleteChatAsync(ChatOptions()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.OpenAIImageGenerationRequestAsync(
            new() { Model = "image-model", Prompt = "A cat" }));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data\":{}}")]
    public async Task Discovery_rejects_invalid_catalog_without_a_static_fallback(string json)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(json)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());
    }

    [Fact]
    public async Task Discovery_preserves_http_errors_and_cancellation()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"message":"invalid key"}}""", HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ListModels());
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);

        using var cancellation = new CancellationTokenSource();
        provider = CreateProvider(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListModels(cancellation.Token));
    }

    [Fact]
    public async Task Chat_preserves_multimodal_messages_native_options_reasoning_and_usage()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.Equal("https://token.sensenova.cn/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("vendor/chat", root.GetProperty("model").GetString());
            Assert.Equal("data:image/png;base64,AQID", root.GetProperty("messages")[0]
                .GetProperty("content")[1].GetProperty("image_url").GetProperty("url").GetString());
            Assert.Equal(17, root.GetProperty("top_k").GetInt32());
            Assert.True(root.GetProperty("future_option").GetProperty("enabled").GetBoolean());
            Assert.False(root.TryGetProperty("temperature", out _));
            Assert.False(root.TryGetProperty("metadata", out _));
            return JsonResponse(Completion);
        });
        var options = ChatOptions();
        options.Messages = [new()
        {
            Role = "user",
            Content = Element(new object[]
            {
                new { type = "text", text = "Describe this" },
                new { type = "image_url", image_url = new { url = "data:image/png;base64,AQID" } }
            })
        }];
        options.AdditionalProperties = new() { ["top_k"] = Element(17) };
        options.SetProviderMetadata(new() { ["sensenova"] = Element(new { future_option = new { enabled = true } }) });

        var response = JsonSerializer.SerializeToElement(await provider.CompleteChatAsync(options));
        Assert.Equal("sensenova/vendor/chat", response.GetProperty("model").GetString());
        Assert.Equal("Thinking", response.GetProperty("choices")[0].GetProperty("message").GetProperty("reasoning").GetString());
        Assert.Equal(5, response.GetProperty("usage").GetProperty("total_tokens").GetInt32());
    }

    [Fact]
    public async Task Streaming_preserves_reasoning_text_and_trailing_usage_through_unified_mapping()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[{"index":0,"delta":{"reasoning":"Thinking"},"finish_reason":""}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[{"index":0,"delta":{"content":"Hello"},"finish_reason":""}]}

                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"vendor/chat","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

                    data: {"choices":[],"usage":{"prompt_tokens":2,"completion_tokens":3,"total_tokens":5}}

                    data: [DONE]

                    """, Encoding.UTF8, "text/event-stream")
            };
        });
        var events = new List<AIStreamEvent>();
        await foreach (var item in provider.StreamUnifiedAsync(new() { ProviderId = "sensenova", Model = "vendor/chat" }))
            events.Add(item);

        Assert.Contains(events, item => item.Event.Type == "reasoning-delta");
        Assert.Contains(events, item => item.Event.Type == "text-delta");
        var finish = Assert.IsType<AIFinishEventData>(Assert.Single(events, item => item.Event.Type == "finish").Event.Data);
        Assert.Equal(2, finish.InputTokens);
        Assert.Equal(3, finish.OutputTokens);
        Assert.Equal(5, finish.TotalTokens);
    }

    [Fact]
    public async Task Chat_errors_and_inflight_cancellation_are_propagated()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"message":"rate limited","type":"rate_limit_error"}}""", HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(ChatOptions()));
        Assert.Contains("rate limited", error.Message);

        using var cancellation = new CancellationTokenSource();
        provider = CreateProvider(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse(Completion);
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.CompleteChatAsync(ChatOptions(), cancellation.Token));
    }

    [Fact]
    public async Task Responses_and_Messages_use_existing_chat_translation()
    {
        var calls = 0;
        var provider = CreateProvider((request, _) =>
        {
            calls++;
            Assert.EndsWith("/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(JsonResponse(Completion));
        });
        var response = await provider.ResponsesAsync(new ResponseRequest { Model = "vendor/chat", Input = "Hi" });
        Assert.Equal("completed", response.Status);
        var message = await provider.MessagesAsync(new MessagesRequest
        {
            Model = "vendor/chat", MaxTokens = 20, Messages = []
        }, []);
        Assert.NotNull(message);
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("url")]
    [InlineData("b64_json")]
    public async Task Native_image_generation_preserves_response_format_and_additional_parameters(string? format)
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.Equal("https://token.sensenova.cn/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization!.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("image-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("seed").GetInt32());
            Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
            if (format is null)
                Assert.False(body.RootElement.TryGetProperty("response_format", out _));
            else
                Assert.Equal(format, body.RootElement.GetProperty("response_format").GetString());
            return JsonResponse("""{"created":1,"data":[{"url":"https://example.invalid/image.png"}]}""");
        });
        var request = new OpenAIImageGenerationRequest
        {
            Model = "image-model", Prompt = "A cat", ResponseFormat = format,
            AdditionalProperties = new() { ["seed"] = Element(7) }
        };
        var response = await provider.OpenAIImageGenerationRequestAsync(request);
        Assert.Equal("https://example.invalid/image.png", Assert.Single(response.Data!).Url);
        Assert.Equal(format, request.ResponseFormat);
    }

    [Theory]
    [InlineData("AQID", "data:image/webp;base64,AQID")]
    [InlineData("data:image/jpeg;base64,AQID", "data:image/jpeg;base64,AQID")]
    public async Task Generic_images_map_base64_usage_and_raw_provider_options(string image, string expected)
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert.Equal("b64_json", body.RootElement.GetProperty("response_format").GetString());
            Assert.Equal(7, body.RootElement.GetProperty("seed").GetInt32());
            Assert.Equal(3, body.RootElement.GetProperty("future_option").GetProperty("steps").GetInt32());
            return JsonResponse($$$"""
                {"created":1,"output_format":"webp","data":[{"b64_json":"{{{image}}}"}],
                 "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
                """);
        });
        var response = await provider.ImageRequest(new ImageRequest
        {
            Model = "image-model", Prompt = "A cat",
            ProviderOptions = new()
            {
                ["sensenova"] = Element(new
                {
                    response_format = "url", seed = 7, future_option = new { steps = 3 }
                })
            }
        });
        Assert.Equal(expected, Assert.Single(response.Images!));
        Assert.Equal("sensenova/image-model", response.Response.ModelId);
        Assert.Equal(3, response.Usage!.TotalTokens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Images_reject_masks_empty_results_and_surface_provider_errors(bool edit)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""{"created":1,"data":[]}""")));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new()
        {
            Model = "image-model", Prompt = "A cat", Mask = new ImageFile { Data = "AQID" }
        }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ImageRequest(new()
        {
            Model = "image-model", Prompt = "A cat",
            Files = edit ? [new ImageFile { Data = "AQID" }] : null
        }));
        provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"message":"permission denied"}}""", HttpStatusCode.Forbidden)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAIImageGenerationRequestAsync(
            new() { Model = "image-model", Prompt = "A cat" }));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("permission denied", error.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("url")]
    [InlineData("b64_json")]
    public async Task Native_edits_forward_url_base64_uploads_and_native_options(string? format)
    {
        var calls = 0;
        var provider = CreateProvider(async (request, ct) =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://token.sensenova.cn/v1/images/edits", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("image-model", root.GetProperty("model").GetString());
            Assert.Equal("Add a hat", root.GetProperty("prompt").GetString());
            Assert.Equal("https://example.invalid/reference.png", root.GetProperty("images")[0].GetProperty("image_url").GetString());
            Assert.Equal("data:image/jpeg;base64,AQID", root.GetProperty("images")[1].GetProperty("image_url").GetString());
            Assert.Equal("data:image/png;base64,AQID", root.GetProperty("images")[2].GetProperty("image_url").GetString());
            Assert.Equal("data:image/webp;base64,AQID", root.GetProperty("images")[3].GetProperty("image_url").GetString());
            Assert.False(root.GetProperty("watermark").GetBoolean());
            Assert.True(root.GetProperty("prompt_extend").GetBoolean());
            Assert.Equal(3, root.GetProperty("future_option").GetProperty("steps").GetInt32());
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal(2, root.GetProperty("n").GetInt32());
            Assert.Equal("auto", root.GetProperty("size").GetString());
            if (format is null)
                Assert.False(root.TryGetProperty("response_format", out _));
            else
                Assert.Equal(format, root.GetProperty("response_format").GetString());
            return JsonResponse("""
                {"created":1,"data":[{"url":"https://example.invalid/edited.png","b64_json":"AQID"}],
                 "usage":{"input_tokens":4,"output_tokens":6,"total_tokens":10}}
                """);
        });
        using var upload = new MemoryStream([1, 2, 3]);
        var response = await provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "image-model", Prompt = "Add a hat", N = 2, Size = "auto",
            Images =
            [
                new() { ImageUrl = "https://example.invalid/reference.png" },
                new() { ImageUrl = "data:image/jpeg;base64,AQID" },
                new() { ImageUrl = "AQID" }
            ],
            ImageFiles = [new FormFile(upload, 0, upload.Length, "image", "reference.webp")
            {
                Headers = new HeaderDictionary(), ContentType = "image/webp"
            }],
            AdditionalProperties = new Dictionary<string, JsonElement>()
            {
                ["watermark"] = Element(false), ["prompt_extend"] = Element(true),
                ["future_option"] = Element(new { steps = 3 }),
                ["n"] = Element(2),
                ["response_format"] = Element(format)
            }.Where(pair => pair.Value.ValueKind != JsonValueKind.Null).ToDictionary()
        });
        Assert.Equal(1, calls);
        Assert.Equal("AQID", Assert.Single(response.Data!).B64Json);
        Assert.Equal("https://example.invalid/edited.png", response.Data![0].Url);
        Assert.Equal(10, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Generic_edits_map_files_and_url_inputs_and_require_base64_results()
    {
        var provider = CreateProvider(async (request, ct) =>
        {
            Assert.EndsWith("/v1/images/edits", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            var root = body.RootElement;
            Assert.Equal("data:image/jpeg;base64,AQID", root.GetProperty("images")[0].GetProperty("image_url").GetString());
            Assert.Equal("https://example.invalid/reference.png", root.GetProperty("images")[1].GetProperty("image_url").GetString());
            Assert.Equal("b64_json", root.GetProperty("response_format").GetString());
            Assert.True(root.GetProperty("prompt_extend").GetBoolean());
            return JsonResponse("""
                {"created":1,"data":[{"b64_json":"data:image/jpeg;base64,AQID"}],
                 "usage":{"input_tokens":1,"output_tokens":2,"total_tokens":3}}
                """);
        });
        var response = await provider.ImageRequest(new()
        {
            Model = "image-model", Prompt = "Add a hat",
            Files =
            [
                new ImageFile { Data = "AQID", MediaType = "image/jpeg" },
                new ImageFileUrl { Url = "https://example.invalid/reference.png" }
            ],
            ProviderOptions = new()
            {
                ["sensenova"] = Element(new { response_format = "url", prompt_extend = true })
            }
        });
        Assert.Equal("data:image/jpeg;base64,AQID", Assert.Single(response.Images!));
        Assert.Equal("sensenova/image-model", response.Response.ModelId);
        Assert.Equal(3, response.Usage!.TotalTokens);
    }

    [Fact]
    public async Task Edits_validate_inputs_and_preserve_errors_and_cancellation()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Unexpected network."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIImageEditRequestAsync(
            new() { Model = "image-model", Prompt = "Edit" }));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.OpenAIImageEditRequestAsync(
            new() { Model = "image-model", Images = [new() { ImageUrl = "AQID" }] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "image-model", Prompt = "Edit",
            Images = [new() { ImageUrl = "AQID" }], Mask = new() { ImageUrl = "AQID" }
        }));
        await Assert.ThrowsAsync<FormatException>(() => provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "image-model", Prompt = "Edit", Images = [new() { ImageUrl = "file:///tmp/image.png" }]
        }));
        provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(
            """{"error":{"message":"model cannot edit"}}""", HttpStatusCode.BadRequest)));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "image-model", Prompt = "Edit", Images = [new() { ImageUrl = "AQID" }]
        }));
        Assert.Equal(HttpStatusCode.BadRequest, error.StatusCode);
        Assert.Contains("model cannot edit", error.Message);
        using var cancellation = new CancellationTokenSource();
        provider = CreateProvider(async (_, ct) =>
        {
            cancellation.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            return JsonResponse("{}");
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.OpenAIImageEditRequestAsync(new()
        {
            Model = "image-model", Prompt = "Edit", Images = [new() { ImageUrl = "AQID" }]
        }, cancellation.Token));
    }

    [Fact]
    public void Provider_is_registered_and_uses_the_existing_header_auth_boundary()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SenseNova-Key"] = " header-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("X-SenseNova-Key", HeaderApiKeyResolver.SupportedProviderHeaders["sensenova"]);
        Assert.Equal("header-key", resolver.Resolve("SENSENOVA"));

        var services = new ServiceCollection();
        services.AddProviders();
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddSingleton<IHttpClientFactory>(new Factory((_, _) => throw new InvalidOperationException("Unexpected network.")));
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("sensenova"));
        var provider = registry.GetModelProvider(scope.ServiceProvider, "sensenova");
        Assert.IsType<SenseNovaProvider>(provider);
        Assert.Same(scope.ServiceProvider.GetRequiredService<SenseNovaProvider>(), provider);
    }

    private static ChatCompletionOptions ChatOptions() => new()
    {
        Model = "vendor/chat",
        Messages = [new() { Role = "user", Content = Element("Hi") }]
    };

    private static SenseNovaProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder,
        KeyResolver? key = null)
        => new(key ?? new("test-key"), new Factory(responder),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement Element<T>(T value) => JsonSerializer.SerializeToElement(value);

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Key { get; set; } = key;
        public string? Resolve(string provider) => Key;
    }

    private sealed class Factory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
