using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.AIML;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.AIML;

public sealed class AIMLProviderImageMetadataPassthroughTests
{
    [Fact]
    public async Task Image_request_flattens_hunyuan_options_and_forwards_unknown_json_at_the_root()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var options = JsonDocument.Parse(
            """
            {
              "hunyuan": {
                "negative_prompt": "text",
                "enable_prompt_expansion": false,
                "enable_safety_checker": true,
                "guidance_scale": 4.5,
                "num_inference_steps": 24,
                "future_hunyuan_config": {
                  "nested": [{"enabled": true}, [null, 2.5, "value"]],
                  "explicit_null": null
                }
              },
              "future_config": {"nested": ["unchanged"]},
              "future_array": [1, false, {"deep": ["unchanged"]}],
              "future_string": "native",
              "future_number": 12345678901234567890.123456789,
              "future_boolean": false,
              "future_null": null,
              "prompt": "must not override request prompt",
              "model": "must not override request model"
            }
            """).RootElement.Clone();
        var request = new ImageRequest
        {
            Model = "hunyuan/hunyuan-image-v3-text-to-image",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = options,
                ["another-provider"] = JsonSerializer.SerializeToElement(new { secret = "not-forwarded" })
            }
        };

        using var clientBoundary = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
        var aimlOptions = clientBoundary.RootElement.GetProperty("providerOptions").GetProperty("aiml");
        Assert.True(aimlOptions.GetProperty("hunyuan").TryGetProperty("future_hunyuan_config", out _));
        Assert.True(aimlOptions.TryGetProperty("future_null", out _));

        await provider.ImageRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("hunyuan/hunyuan-image-v3-text-to-image", root.GetProperty("model").GetString());
        Assert.Equal("A city at sunset", root.GetProperty("prompt").GetString());
        Assert.Equal("text", root.GetProperty("negative_prompt").GetString());
        Assert.False(root.GetProperty("enable_prompt_expansion").GetBoolean());
        Assert.True(root.GetProperty("enable_safety_checker").GetBoolean());
        Assert.Equal(4.5, root.GetProperty("guidance_scale").GetDouble());
        Assert.Equal(24, root.GetProperty("num_inference_steps").GetInt32());
        Assert.True(root.GetProperty("sync_mode").GetBoolean());
        Assert.True(root.GetProperty("future_hunyuan_config").GetProperty("nested")[0].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_hunyuan_config").GetProperty("nested")[1][0].ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_hunyuan_config").GetProperty("explicit_null").ValueKind);
        Assert.Equal("unchanged", root.GetProperty("future_config").GetProperty("nested")[0].GetString());
        Assert.Equal("unchanged", root.GetProperty("future_array")[2].GetProperty("deep")[0].GetString());
        Assert.Equal("native", root.GetProperty("future_string").GetString());
        Assert.Equal("12345678901234567890.123456789", root.GetProperty("future_number").GetRawText());
        Assert.False(root.GetProperty("future_boolean").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("aiml", out _));
        Assert.False(root.TryGetProperty("hunyuan", out _));
        Assert.False(root.TryGetProperty("another-provider", out _));
        Assert.False(root.TryGetProperty("secret", out _));
    }

    [Fact]
    public async Task Image_request_forwards_unknown_options_and_preserves_optional_field_omission()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });
        var request = new ImageRequest
        {
            Model = "alibaba/z-image-turbo",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = JsonDocument.Parse(
                    """{"future_object":{"nested":[1,{"value":true}]},"future_null":null}""")
                    .RootElement.Clone()
            }
        };

        await provider.ImageRequest(request);

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("alibaba/z-image-turbo", root.GetProperty("model").GetString());
        Assert.Equal("A city at sunset", root.GetProperty("prompt").GetString());
        Assert.Equal("png", root.GetProperty("output_format").GetString());
        Assert.True(root.GetProperty("future_object").GetProperty("nested")[1].GetProperty("value").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_null").ValueKind);
        Assert.False(root.TryGetProperty("seed", out _));
        Assert.False(root.TryGetProperty("image_size", out _));
        Assert.False(root.TryGetProperty("providerOptions", out _));
        Assert.False(root.TryGetProperty("aiml", out _));
    }

    [Fact]
    public async Task Image_request_flattens_openai_options_without_filtering_unknown_fields()
    {
        string? requestBody = null;
        var provider = CreateProvider(request =>
        {
            requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonResponse("""{"data":[{"b64_json":"AQID"}]}""");
        });

        await provider.ImageRequest(new ImageRequest
        {
            Model = "openai/gpt-image-1",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = JsonDocument.Parse(
                    """{"openai":{"quality":"high","future_setting":{"nested":[true,null]}}}""")
                    .RootElement.Clone()
            }
        });

        using var payload = JsonDocument.Parse(Assert.IsType<string>(requestBody));
        var root = payload.RootElement;
        Assert.Equal("high", root.GetProperty("quality").GetString());
        Assert.True(root.GetProperty("future_setting").GetProperty("nested")[0].GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_setting").GetProperty("nested")[1].ValueKind);
        Assert.False(root.TryGetProperty("openai", out _));
    }

    [Fact]
    public async Task Image_request_preserves_validation_for_known_hunyuan_settings()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<JsonException>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "hunyuan/hunyuan-image-v3-text-to-image",
            Prompt = "A city at sunset",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["aiml"] = JsonDocument.Parse("""{"hunyuan":{"guidance_scale":"invalid"}}""")
                    .RootElement.Clone()
            }
        }));
    }

    [Fact]
    public async Task Image_request_preserves_upstream_error_details()
    {
        var provider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("image failure", Encoding.UTF8)
        });

        var error = await Assert.ThrowsAsync<Exception>(() => provider.ImageRequest(new ImageRequest
        {
            Model = "hunyuan/hunyuan-image-v3-text-to-image",
            Prompt = "A city at sunset"
        }));

        Assert.Equal("BadRequest: image failure", error.Message);
    }

    private static AIMLProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new SingleClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
