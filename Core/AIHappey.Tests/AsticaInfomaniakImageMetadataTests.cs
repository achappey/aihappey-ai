using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Astica;
using AIHappey.Core.Providers.Infomaniak;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests;

public sealed class AsticaInfomaniakImageMetadataTests
{
    [Fact]
    public async Task Astica_speech_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/api/tts", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"status":"success","audio_b64":"AQID"}""");
        });
        var metadata = ParseElement("""
            {
              "timestamps": false,
              "prompt": "  warm and expressive  ",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "tkn": "metadata-key",
              "text": "metadata text",
              "voice": "metadata voice",
              "stream": true
            }
            """);
        var request = new SpeechRequest
        {
            Model = "astica/expressive",
            Text = "hello",
            ProviderOptions = new()
            {
                ["astica"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var clientRequest = JsonSerializer.SerializeToElement(request);
        Assert.True(JsonElement.DeepEquals(metadata, clientRequest.GetProperty("providerOptions").GetProperty("astica")));
        Assert.True(clientRequest.GetProperty("providerOptions").TryGetProperty("another-provider", out _));

        await CreateAsticaProvider(client).SpeechRequest(request);

        Assert.Equal("test-key", payload.GetProperty("tkn").GetString());
        Assert.Equal("hello", payload.GetProperty("text").GetString());
        Assert.Equal("expressive", payload.GetProperty("voice").GetString());
        Assert.False(payload.GetProperty("stream").GetBoolean());
        Assert.False(payload.GetProperty("timestamps").GetBoolean());
        Assert.Equal("warm and expressive", payload.GetProperty("prompt").GetString());
        foreach (var name in new[]
                 {
                     "future_object", "future_array", "future_string", "future_number", "future_boolean", "future_null"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("astica", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["astica"]));
    }

    [Fact]
    public async Task Astica_speech_omits_null_known_options_but_preserves_unknown_nulls()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"status":"success","audio_b64":"AQID"}""");
        });
        var request = new SpeechRequest
        {
            Model = "astica/expressive",
            Text = "hello",
            ProviderOptions = new()
            {
                ["astica"] = ParseElement("""{"timestamps":null,"prompt":" ","future_null":null}""")
            }
        };

        await CreateAsticaProvider(client).SpeechRequest(request);

        Assert.False(payload.TryGetProperty("timestamps", out _));
        Assert.False(payload.TryGetProperty("prompt", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
    }

    [Fact]
    public async Task Astica_speech_invalid_known_option_type_still_fails_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new SpeechRequest
        {
            Model = "astica/expressive",
            Text = "hello",
            ProviderOptions = new()
            {
                ["astica"] = ParseElement("""{"timestamps":"yes"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => CreateAsticaProvider(client).SpeechRequest(request));
    }

    [Fact]
    public async Task Astica_speech_upstream_error_message_remains_compatible()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream error")
        });
        var request = new SpeechRequest
        {
            Model = "astica/expressive",
            Text = "hello"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateAsticaProvider(client).SpeechRequest(request));

        Assert.Equal("Astica request 'api/tts' failed (502): upstream error", error.Message);
    }

    [Fact]
    public async Task Astica_image_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                Assert.Equal("/generate_image", message.RequestUri!.AbsolutePath);
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"status":"success","output":"https://images.example/output.png"}""");
            }

            Assert.Equal(HttpMethod.Get, message.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var metadata = ParseElement("""
            {
              "prompt_negative": "  no text  ",
              "generate_quality": " FAST ",
              "generate_lossless": "1",
              "moderate": 0,
              "low_priority": 1,
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
            Model = "astica/realistic",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["astica"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateAsticaProvider(client).ImageRequest(request);

        Assert.Equal("test-key", payload.GetProperty("tkn").GetString());
        Assert.Equal("realistic", payload.GetProperty("modelVersion").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("no text", payload.GetProperty("prompt_negative").GetString());
        Assert.Equal("fast", payload.GetProperty("generate_quality").GetString());
        Assert.Equal(1, payload.GetProperty("generate_lossless").GetInt32());
        Assert.Equal(0, payload.GetProperty("moderate").GetInt32());
        Assert.Equal(1, payload.GetProperty("low_priority").GetInt32());
        AssertProviderFieldsForwarded(metadata, payload,
            "prompt_negative", "generate_quality", "generate_lossless", "moderate", "low_priority");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("astica", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["astica"]));
    }

    [Fact]
    public async Task Astica_known_null_options_keep_their_serialized_nulls()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Post)
            {
                payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return JsonResponse("""{"status":"success","output":"https://images.example/output.png"}""");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3])
            };
        });
        var request = new ImageRequest
        {
            Model = "astica/realistic",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["astica"] = ParseElement(
                    """{"prompt_negative":null,"generate_quality":" ","generate_lossless":null,"moderate":null,"low_priority":null,"future_null":null}""")
            }
        };

        await CreateAsticaProvider(client).ImageRequest(request);

        foreach (var name in new[] { "prompt_negative", "generate_quality", "generate_lossless", "moderate", "low_priority" })
            Assert.Equal(JsonValueKind.Null, payload.GetProperty(name).ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
    }

    [Fact]
    public async Task Astica_invalid_known_quality_still_fails_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new ImageRequest
        {
            Model = "astica/realistic",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["astica"] = ParseElement("""{"generate_quality":"ultra"}""")
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateAsticaProvider(client).ImageRequest(request));
    }

    [Fact]
    public async Task Astica_invalid_known_option_type_still_fails_before_sending()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new ImageRequest
        {
            Model = "astica/realistic",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["astica"] = ParseElement("""{"generate_lossless":"png"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => CreateAsticaProvider(client).ImageRequest(request));
    }

    [Fact]
    public async Task Astica_upstream_error_message_remains_compatible()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)
        {
            Content = new StringContent("upstream error")
        });
        var request = new ImageRequest
        {
            Model = "astica/realistic",
            Prompt = "A landscape"
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CreateAsticaProvider(client).ImageRequest(request));

        Assert.Equal("Astica image request failed (502): upstream error", error.Message);
    }

    [Fact]
    public async Task Infomaniak_openai_image_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return InfomaniakProductsResponse();

            Assert.Equal("/1/ai/71/openai/images/generations", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var metadata = ParseElement("""
            {
              "negative_prompt": "no text",
              "quality": "high",
              "style": "vivid",
              "sync": false,
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "prompt": "metadata prompt",
              "model": "metadata-model",
              "response_format": "url"
            }
            """);
        var request = new ImageRequest
        {
            Model = "infomaniak/image-model",
            Prompt = "A mountain",
            N = 2,
            Size = "640:480",
            ProviderOptions = new()
            {
                ["infomaniak"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateInfomaniakProvider(client).ImageRequest(request);

        Assert.Equal("infomaniak/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A mountain", payload.GetProperty("prompt").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(2, payload.GetProperty("n").GetInt32());
        Assert.Equal("640x480", payload.GetProperty("size").GetString());
        Assert.Equal("no text", payload.GetProperty("negative_prompt").GetString());
        Assert.Equal("high", payload.GetProperty("quality").GetString());
        Assert.Equal("vivid", payload.GetProperty("style").GetString());
        Assert.False(payload.GetProperty("sync").GetBoolean());
        AssertProviderFieldsForwarded(metadata, payload,
            "negative_prompt", "quality", "style", "sync", "prompt", "model", "response_format");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("infomaniak", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["infomaniak"]));
    }

    [Fact]
    public async Task Infomaniak_known_null_options_are_omitted_but_unknown_nulls_are_forwarded()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return InfomaniakProductsResponse();

            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var request = new ImageRequest
        {
            Model = "infomaniak/image-model",
            Prompt = "A mountain",
            ProviderOptions = new()
            {
                ["infomaniak"] = ParseElement(
                    """{"negative_prompt":null,"quality":"","style":null,"sync":null,"future_null":null}""")
            }
        };

        await CreateInfomaniakProvider(client).ImageRequest(request);

        foreach (var name in new[] { "negative_prompt", "quality", "style", "sync" })
            Assert.False(payload.TryGetProperty(name, out _), name);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
    }

    [Fact]
    public async Task Infomaniak_photo_maker_keeps_its_endpoint_and_file_mapping()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            if (message.Method == HttpMethod.Get)
                return InfomaniakProductsResponse();

            Assert.Equal("/1/ai/71/images/generations/photo_maker", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var request = new ImageRequest
        {
            Model = "infomaniak/photomaker-v1",
            Prompt = "A portrait",
            Files = [new ImageFile { Data = "data:image/png;base64,AA==" }]
        };

        await CreateInfomaniakProvider(client).ImageRequest(request);

        Assert.Equal("A portrait", payload.GetProperty("prompt").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal("data:image/png;base64,AA==", payload.GetProperty("images")[0].GetString());
        Assert.False(payload.TryGetProperty("model", out _));
    }

    [Fact]
    public async Task Infomaniak_upstream_error_message_remains_compatible()
    {
        using var client = CreateClient(message => message.Method == HttpMethod.Get
            ? InfomaniakProductsResponse()
            : new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("upstream error")
            });
        var request = new ImageRequest
        {
            Model = "infomaniak/image-model",
            Prompt = "A mountain"
        };

        var error = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateInfomaniakProvider(client).ImageRequest(request));

        Assert.Contains("Infomaniak image generation failed: 400", error.Message);
        Assert.Contains("upstream error", error.Message);
    }

    private static void AssertProviderFieldsForwarded(
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

    private static AsticaProvider CreateAsticaProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static InfomaniakProvider CreateInfomaniakProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage InfomaniakProductsResponse()
        => JsonResponse("""{"result":"success","data":[{"status":"ok","product_id":71}]}""");

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
