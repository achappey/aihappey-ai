using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MegaNova;
using AIHappey.Core.Providers.Nebius;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests;

public sealed class ProviderImageMetadataPassthroughTests
{
    [Fact]
    public async Task MegaNova_image_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/images/generations", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var metadata = ParseElement("""
            {
              "num_steps": 23,
              "guidance_scale": 2.5,
              "negative_prompt": "no text",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt",
              "width": 10
            }
            """);
        var request = new ImageRequest
        {
            Model = "meganova/image-model",
            Prompt = "A landscape",
            Size = "512x768",
            ProviderOptions = new()
            {
                ["meganova"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await CreateMegaNovaProvider(client).ImageRequest(request);

        Assert.Equal("meganova/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A landscape", payload.GetProperty("prompt").GetString());
        Assert.Equal(512, payload.GetProperty("width").GetInt32());
        Assert.Equal(768, payload.GetProperty("height").GetInt32());
        Assert.Equal(23, payload.GetProperty("num_steps").GetInt32());
        Assert.Equal(2.5f, payload.GetProperty("guidance_scale").GetSingle());
        Assert.Equal("no text", payload.GetProperty("negative_prompt").GetString());
        AssertProviderFieldsForwarded(metadata, payload,
            "num_steps", "guidance_scale", "negative_prompt", "model", "prompt", "width");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("meganova", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["meganova"]));
    }

    [Fact]
    public async Task MegaNova_known_nulls_preserve_null_serialization_and_invalid_known_values_still_fail()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "meganova/image-model",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["meganova"] = ParseElement(
                    """{"num_steps":null,"guidance_scale":null,"negative_prompt":null,"future_null":null}""")
            }
        };

        await CreateMegaNovaProvider(client).ImageRequest(request);

        Assert.Equal(JsonValueKind.Null, payload.GetProperty("num_steps").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("guidance_scale").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("negative_prompt").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("width").ValueKind);
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("height").ValueKind);

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["meganova"] = ParseElement("""{"num_steps":"many"}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateMegaNovaProvider(invalidClient).ImageRequest(request));
    }

    [Fact]
    public async Task Nebius_image_options_merge_at_root_and_preserve_unknown_json()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            Assert.Equal("/v1/images/generations", message.RequestUri!.AbsolutePath);
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var metadata = ParseElement("""
            {
              "num_inference_steps": 17,
              "guidance_scale": 3.5,
              "negative_prompt": "no blur",
              "response_extension": "jpg",
              "future_object": {"nested": [{"value": 1}, [true, null, "text"]]},
              "future_array": [1, {"more": [false, 2.5]}, []],
              "future_string": "unchanged",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "model": "metadata-model",
              "prompt": "metadata-prompt",
              "response_format": "url"
            }
            """);
        var request = new ImageRequest
        {
            Model = "nebius/image-model",
            Prompt = "A mountain",
            Size = "640x480",
            Seed = 42,
            ProviderOptions = new()
            {
                ["nebius"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        var response = await CreateNebiusProvider(client).ImageRequest(request);

        Assert.Equal("nebius/image-model", payload.GetProperty("model").GetString());
        Assert.Equal("A mountain", payload.GetProperty("prompt").GetString());
        Assert.Equal(640, payload.GetProperty("width").GetInt32());
        Assert.Equal(480, payload.GetProperty("height").GetInt32());
        Assert.Equal(42, payload.GetProperty("seed").GetInt32());
        Assert.Equal(17, payload.GetProperty("num_inference_steps").GetInt32());
        Assert.Equal(3.5f, payload.GetProperty("guidance_scale").GetSingle());
        Assert.Equal("no blur", payload.GetProperty("negative_prompt").GetString());
        Assert.Equal("jpg", payload.GetProperty("response_extension").GetString());
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal("data:image/jpeg;base64,AQID", Assert.Single(response.Images!));
        AssertProviderFieldsForwarded(metadata, payload,
            "num_inference_steps", "guidance_scale", "negative_prompt", "response_extension",
            "model", "prompt", "response_format");
        Assert.False(payload.TryGetProperty("providerOptions", out _));
        Assert.False(payload.TryGetProperty("nebius", out _));
        Assert.False(payload.TryGetProperty("another-provider", out _));
        Assert.False(payload.TryGetProperty("secret", out _));
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["nebius"]));
    }

    [Fact]
    public async Task Nebius_defaults_and_known_null_omission_remain_compatible()
    {
        JsonElement payload = default;
        using var client = CreateClient(message =>
        {
            payload = ParseElement(message.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            return SuccessfulImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "nebius/image-model",
            Prompt = "A mountain",
            ProviderOptions = new()
            {
                ["nebius"] = ParseElement(
                    """{"num_inference_steps":null,"guidance_scale":null,"negative_prompt":null,"response_extension":null,"future_null":null}""")
            }
        };

        var response = await CreateNebiusProvider(client).ImageRequest(request);

        Assert.False(payload.TryGetProperty("num_inference_steps", out _));
        Assert.False(payload.TryGetProperty("guidance_scale", out _));
        Assert.False(payload.TryGetProperty("negative_prompt", out _));
        Assert.False(payload.TryGetProperty("response_extension", out _));
        Assert.Equal("b64_json", payload.GetProperty("response_format").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("future_null").ValueKind);
        Assert.Equal("data:image/webp;base64,AQID", Assert.Single(response.Images!));

        using var invalidClient = CreateClient(_ => throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["nebius"] = ParseElement("""{"guidance_scale":"strong"}""");
        await Assert.ThrowsAsync<JsonException>(() => CreateNebiusProvider(invalidClient).ImageRequest(request));
    }

    [Theory]
    [InlineData("meganova")]
    [InlineData("nebius")]
    public async Task Upstream_errors_keep_the_existing_exception_message(string providerId)
    {
        using var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var request = new ImageRequest
        {
            Model = $"{providerId}/image-model",
            Prompt = "An image"
        };
        var providerCall = providerId == "meganova"
            ? CreateMegaNovaProvider(client).ImageRequest(request)
            : CreateNebiusProvider(client).ImageRequest(request);

        var error = await Assert.ThrowsAsync<Exception>(() => providerCall);

        Assert.Equal("BadRequest: upstream error", error.Message);
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

    private static MegaNovaProvider CreateMegaNovaProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));

    private static NebiusProvider CreateNebiusProvider(HttpClient client)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(client),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage SuccessfulImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """{"data":[{"b64_json":"AQID"}]}""",
                Encoding.UTF8,
                "application/json")
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
