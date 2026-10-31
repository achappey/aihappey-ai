using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Reve;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Reve;

public sealed class ReveProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Image_request_forwards_unknown_provider_metadata_at_root_and_preserves_known_mapping()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v2/image/create", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var metadata = ParseElement("""
            {
              "test_time_scaling": 1.5,
              "postprocessing": [{"process":"upscale","upscale_factor":2}],
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt"
            }
            """);
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["reve"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };
        var serializedRequest = JsonSerializer.SerializeToElement(request);

        Assert.True(JsonElement.DeepEquals(metadata,
            serializedRequest.GetProperty("providerOptions").GetProperty("reve")));

        await CreateReveProvider(client).ImageRequest(request);

        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("metadata-model", payload.GetProperty("model").GetString());
        Assert.Equal("reve-v2-create@260601", payload.GetProperty("version").GetString());
        Assert.Equal("upscale", payload.GetProperty("postprocessing")[0].GetProperty("process").GetString());
        Assert.Equal(2, payload.GetProperty("postprocessing")[0].GetProperty("upscale_factor").GetInt32());
        Assert.Equal(
            new[]
            {
                "future_array", "future_boolean", "future_null", "future_number", "future_object",
                "future_string", "model", "postprocessing", "prompt", "test_time_scaling", "version"
            },
            payload.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        foreach (var name in new[]
                 {
                     "test_time_scaling", "future_object", "future_array", "future_string",
                     "future_number", "future_boolean", "future_null"
                 })
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty(name), payload.GetProperty(name)), name);

        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("reve", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["reve"]));
    }

    [Fact]
    public async Task Image_request_preserves_latest_version_default_without_provider_metadata()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "latest",
            Prompt = "A landscape"
        };

        await CreateReveProvider(client).ImageRequest(request);

        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("reve-v2-create@260601", payload.GetProperty("version").GetString());
        Assert.Equal(new[] { "prompt", "version" }, payload.EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Image_request_omits_null_mapped_options_but_forwards_unknown_nulls()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["reve"] = ParseElement("""{"postprocessing":null,"future_null":null}""")
            }
        };

        await CreateReveProvider(client).ImageRequest(request);

        Assert.False(payload.TryGetProperty("postprocessing", out _));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(3, payload.EnumerateObject().Count());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Image_request_preserves_metadata_options_unless_explicit_top_level_values_override_them(
        bool hasAspectRatio,
        bool hasFiles)
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var metadata = ParseElement("""
            {
              "aspect_ratio": "1:1",
              "references": [{"data":"metadata-reference","future":{"values":[1,null,true]}}],
              "postprocessing": [{"process":"upscale","upscale_factor":2}],
              "future_null": null
            }
            """);
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            AspectRatio = hasAspectRatio ? "16:9" : null,
            Files = hasFiles
                ? [new ImageFile { MediaType = "image/png", Data = "data:image/png;base64,AQID" },
                   new ImageFileUrl { Url = "https://example.com/reference.png" }]
                : null,
            ProviderOptions = new() { ["reve"] = metadata }
        };

        await CreateReveProvider(client).ImageRequest(request);

        Assert.Equal(hasAspectRatio ? "16:9" : "1:1", payload.GetProperty("aspect_ratio").GetString());
        if (hasFiles)
        {
            Assert.True(JsonElement.DeepEquals(
                ParseElement("""[{"data":"AQID"},{"data":"https://example.com/reference.png"}]"""),
                payload.GetProperty("references")));
        }
        else
        {
            Assert.True(JsonElement.DeepEquals(metadata.GetProperty("references"), payload.GetProperty("references")));
        }
        Assert.True(JsonElement.DeepEquals(metadata.GetProperty("postprocessing"), payload.GetProperty("postprocessing")));
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["reve"]));
    }

    [Fact]
    public async Task Image_request_preserves_explicit_nulls_in_unmapped_optional_metadata()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["reve"] = ParseElement("""{"aspect_ratio":null,"references":null,"postprocessing":null,"future_null":null}""")
            }
        };

        await CreateReveProvider(client).ImageRequest(request);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("aspect_ratio").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("references").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.False(payload.TryGetProperty("postprocessing", out _));
        Assert.Equal(5, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Image_request_preserves_explicit_empty_collections_and_top_level_file_precedence()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            Files = [],
            ProviderOptions = new()
            {
                ["reve"] = ParseElement("""{"references":[{"data":"metadata-reference"}],"postprocessing":[]}""")
            }
        };

        await CreateReveProvider(client).ImageRequest(request);

        Assert.Empty(payload.GetProperty("references").EnumerateArray());
        Assert.Empty(payload.GetProperty("postprocessing").EnumerateArray());
        Assert.False(payload.TryGetProperty("aspect_ratio", out _));
        Assert.Equal(4, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task OpenAI_image_generation_preserves_raw_optional_additional_properties()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v2/image/create", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new OpenAIImageGenerationRequest
        {
            Model = "latest",
            Prompt = "A landscape",
            AdditionalProperties = new()
            {
                ["aspect_ratio"] = ParseElement("\"16:9\""),
                ["references"] = ParseElement("""[{"data":"AQID","future_null":null}]"""),
                ["future_null"] = ParseElement("null")
            }
        };

        await CreateReveProvider(client).OpenAIImageGenerationRequestAsync(request);

        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal("reve-v2-create@260601", payload.GetProperty("version").GetString());
        foreach (var (name, value) in request.AdditionalProperties)
            Assert.True(JsonElement.DeepEquals(value, payload.GetProperty(name)), name);
        Assert.False(payload.TryGetProperty("postprocessing", out _));
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.Equal(5, payload.EnumerateObject().Count());
    }

    [Fact]
    public async Task Image_request_preserves_unknown_postprocessing_fields_and_omits_null_mapped_fields()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["reve"] = ParseElement(
                    """{"postprocessing":[{"process":null,"max_dim":null,"future":{"values":[1,null,true]},"future_null":null}]}""")
            }
        };

        await CreateReveProvider(client).ImageRequest(request);

        var postprocessing = payload.GetProperty("postprocessing")[0];
        Assert.False(postprocessing.TryGetProperty("process", out _));
        Assert.False(postprocessing.TryGetProperty("max_dim", out _));
        Assert.True(JsonElement.DeepEquals(
            request.ProviderOptions["reve"].GetProperty("postprocessing")[0].GetProperty("future"),
            postprocessing.GetProperty("future")));
        Assert.Equal(JsonValueKind.Null, postprocessing.GetProperty("future_null").ValueKind);
    }

    [Fact]
    public async Task Image_request_keeps_invalid_known_metadata_error_behavior()
    {
        using var client = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["reve"] = ParseElement("""{"postprocessing":"invalid"}""")
            }
        };

        await Assert.ThrowsAsync<JsonException>(() => CreateReveProvider(client).ImageRequest(request));
    }

    [Fact]
    public async Task Image_request_keeps_upstream_error_details()
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        });
        var request = new ImageRequest
        {
            Model = "reve-v2-create@260601",
            Prompt = "A landscape"
        };

        var exception = await Assert.ThrowsAsync<Exception>(() =>
            CreateReveProvider(client).ImageRequest(request));

        Assert.Equal("BadRequest: upstream failure", exception.Message);
    }

    private static ReveProvider CreateReveProvider(HttpClient client)
        => new(new StaticApiKeyResolver(), new StaticHttpClientFactory(client));

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage SuccessfulImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"image":"AQID","credits_used":0}""", Encoding.UTF8, "application/json")
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
