using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Lingvanex;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Lingvanex;

public sealed class LingvanexProviderMetadataPassthroughTests
{
    [Fact]
    public async Task Stream_request_preserves_keyed_client_metadata_and_passes_raw_fields_to_upstream_root()
    {
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"err":null,"result":"bonjour"}""");
        });
        var metadata = JsonDocument.Parse("""
            {
              "translateMode": "html",
              "enableTransliteration": true,
              "future_option": {
                "values": [1, null, {"enabled": true}],
                "explicit_null": null
              },
              "feature_flag": false
            }
            """);
        using (metadata)
        {
            var request = CreateRequest(metadata.RootElement.Clone());

            using var clientRequest = JsonDocument.Parse(JsonSerializer.Serialize(request, JsonSerializerOptions.Web));
            var clientMetadata = clientRequest.RootElement
                .GetProperty("providerMetadata")
                .GetProperty("lingvanex");
            Assert.Equal(
                JsonValueKind.Array,
                clientMetadata.GetProperty("future_option").GetProperty("values").ValueKind);
            Assert.Equal(
                JsonValueKind.Null,
                clientMetadata.GetProperty("future_option").GetProperty("explicit_null").ValueKind);

            await ConsumeAsync(provider.StreamAsync(request));
        }

        using var upstream = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        var root = upstream.RootElement;
        Assert.Equal("api", root.GetProperty("platform").GetString());
        Assert.Equal("en_GB", root.GetProperty("to").GetString());
        Assert.Equal("hello", root.GetProperty("data").GetString());
        Assert.Equal("html", root.GetProperty("translateMode").GetString());
        Assert.True(root.GetProperty("enableTransliteration").GetBoolean());
        Assert.Equal(1, root.GetProperty("future_option").GetProperty("values")[0].GetInt32());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_option").GetProperty("values")[1].ValueKind);
        Assert.True(root.GetProperty("future_option").GetProperty("values")[2].GetProperty("enabled").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("future_option").GetProperty("explicit_null").ValueKind);
        Assert.False(root.GetProperty("feature_flag").GetBoolean());
        Assert.False(root.TryGetProperty("providerMetadata", out _));
        Assert.False(root.TryGetProperty("lingvanex", out _));
    }

    [Fact]
    public async Task Stream_request_keeps_defaults_omission_and_required_payload_precedence()
    {
        string? capturedBody = null;
        var provider = CreateProvider(async request =>
        {
            capturedBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{"err":null,"result":"bonjour"}""");
        });
        var rawOptions = JsonSerializer.SerializeToElement(new
        {
            translateMode = " ",
            enableTransliteration = (bool?)null,
            platform = "client-platform",
            to = "fr_FR",
            data = "client-data"
        });

        await ConsumeAsync(provider.StreamAsync(CreateRequest(rawOptions)));

        using var upstream = JsonDocument.Parse(Assert.IsType<string>(capturedBody));
        var root = upstream.RootElement;
        Assert.Equal("api", root.GetProperty("platform").GetString());
        Assert.Equal("en_GB", root.GetProperty("to").GetString());
        Assert.Equal("hello", root.GetProperty("data").GetString());
        Assert.False(root.TryGetProperty("translateMode", out _));
        Assert.False(root.TryGetProperty("enableTransliteration", out _));
        Assert.Equal(3, root.EnumerateObject().Count());
    }

    [Fact]
    public async Task Stream_request_preserves_invalid_known_metadata_error_behavior()
    {
        var provider = CreateProvider(_ => throw new InvalidOperationException("Request should not be sent."));
        var request = CreateRequest(JsonSerializer.SerializeToElement(new { translateMode = 5 }));

        await Assert.ThrowsAsync<JsonException>(() => ConsumeAsync(provider.StreamAsync(request)));
    }

    [Fact]
    public async Task Stream_request_keeps_upstream_error_details_in_error_part()
    {
        var provider = CreateProvider(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("upstream failure", Encoding.UTF8, "text/plain")
        }));

        var parts = await CollectAsync(provider.StreamAsync(CreateRequest(
            JsonSerializer.SerializeToElement(new { future_option = true }))));

        var error = Assert.IsType<ErrorUIPart>(Assert.Single(parts));
        Assert.Contains("Lingvanex translate failed (400): upstream failure", error.ErrorText);
    }

    private static ChatRequest CreateRequest(JsonElement metadata)
        => new()
        {
            Model = "translate/en_GB",
            ProviderMetadata = new Dictionary<string, JsonElement>
            {
                ["lingvanex"] = metadata
            },
            Messages =
            [
                new UIMessage
                {
                    Id = "user-message",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "hello" }]
                }
            ]
        };

    private static async Task ConsumeAsync(IAsyncEnumerable<UIMessagePart> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private static async Task<List<UIMessagePart>> CollectAsync(IAsyncEnumerable<UIMessagePart> stream)
    {
        var parts = new List<UIMessagePart>();
        await foreach (var part in stream)
            parts.Add(part);
        return parts;
    }

    private static LingvanexProvider CreateProvider(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(
            new StaticApiKeyResolver(),
            new StaticHttpClientFactory(new HttpClient(new CapturingHandler(responder))));

    private static HttpResponseMessage JsonResponse(string body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-api-key";
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class CapturingHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
