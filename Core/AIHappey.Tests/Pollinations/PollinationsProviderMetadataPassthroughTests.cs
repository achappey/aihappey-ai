using System.Net;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Tests.Pollinations;

public sealed class PollinationsProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Image_request_forwards_unknown_options_and_maps_known_values()
    {
        Uri? upstreamUri = null;
        var provider = CreateProvider(request =>
        {
            upstreamUri = request.RequestUri;
            return ImageResponse();
        });
        var metadata = ParseElement("""
            {
              "enhance": true,
              "private": true,
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
            Model = "flux",
            Prompt = "A landscape",
            Size = "512x768",
            Seed = 7,
            ProviderOptions = new()
            {
                ["pollinations"] = metadata,
                ["another-provider"] = ParseElement("""{"secret":"not-forwarded"}""")
            }
        };

        await provider.ImageRequest(request);

        Assert.NotNull(upstreamUri);
        var query = ParseQuery(upstreamUri!.Query);
        Assert.Equal("flux", query["model"]);
        Assert.Equal("512", query["width"]);
        Assert.Equal("768", query["height"]);
        Assert.Equal("7", query["seed"]);
        Assert.Equal("true", query["enhance"]);
        Assert.Equal("true", query["private"]);
        Assert.Equal(metadata.GetProperty("future_object").GetRawText(), query["future_object"]);
        Assert.Equal(metadata.GetProperty("future_array").GetRawText(), query["future_array"]);
        Assert.Equal("unchanged", query["future_string"]);
        Assert.Equal("12345678901234567890.123456789", query["future_number"]);
        Assert.Equal("false", query["future_boolean"]);
        Assert.Equal("null", query["future_null"]);
        Assert.Equal(
            new[]
            {
                "enhance", "future_array", "future_boolean", "future_null", "future_number",
                "future_object", "future_string", "height", "model", "private", "seed", "width"
            },
            query.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.True(JsonElement.DeepEquals(metadata, request.ProviderOptions["pollinations"]));
    }

    [Fact]
    public async Task Image_request_preserves_known_omissions_and_rejects_invalid_known_types()
    {
        Uri? upstreamUri = null;
        var provider = CreateProvider(request =>
        {
            upstreamUri = request.RequestUri;
            return ImageResponse();
        });
        var request = new ImageRequest
        {
            Model = "flux",
            Prompt = "A landscape",
            ProviderOptions = new()
            {
                ["pollinations"] = ParseElement(
                    """{"enhance":false,"private":null,"future_null":null}""")
            }
        };

        await provider.ImageRequest(request);

        var query = ParseQuery(upstreamUri!.Query);
        Assert.False(query.ContainsKey("enhance"));
        Assert.False(query.ContainsKey("private"));
        Assert.Equal("null", query["future_null"]);

        var invalidProvider = CreateProvider(_ =>
            throw new InvalidOperationException("Request should not be sent."));
        request.ProviderOptions["pollinations"] = ParseElement("""{"enhance":"true"}""");
        await Assert.ThrowsAsync<JsonException>(() => invalidProvider.ImageRequest(request));
    }

    [Fact]
    public async Task Image_request_keeps_validation_and_upstream_error_behavior()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ImageRequest(new ImageRequest { Prompt = " " }));

        var failingProvider = CreateProvider(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream error")
        });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingProvider.ImageRequest(new ImageRequest { Prompt = "A landscape" }));

        Assert.Equal("Pollinations image error: upstream error", error.Message);
    }

    private static Dictionary<string, string> ParseQuery(string query)
        => query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);

    private static PollinationsProvider CreateProvider(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new StaticApiKeyResolver(), new HttpContextAccessor(),
            new StaticHttpClientFactory(new HttpClient(new DelegateHttpMessageHandler(responder))));

    private static JsonElement ParseElement(string json)
        => JsonDocument.Parse(json).RootElement.Clone();

    private static HttpResponseMessage ImageResponse()
        => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3])
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
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
