using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Deepgram;

namespace AIHappey.Tests.Deepgram;

public sealed class DeepgramProviderRealtimeTests
{
    [Fact]
    public async Task GetRealtimeToken_uses_default_ttl_and_maps_expiry_in_seconds()
    {
        string? requestBody = null;
        var provider = CreateProvider(async request =>
        {
            requestBody = await request.Content!.ReadAsStringAsync();
            return JsonResponse("""{ "access_token": "token", "expires_in": 30 }""");
        });

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = await provider.GetRealtimeToken(new RealtimeRequest { Model = "nova-3" });

        Assert.Empty(JsonDocument.Parse(requestBody!).RootElement.EnumerateObject());
        Assert.InRange(response.ExpiresAt, before + 30, before + 31);
    }

    [Fact]
    public async Task GetRealtimeToken_forwards_documented_ttl_seconds()
    {
        var provider = CreateProvider(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(120, body.RootElement.GetProperty("ttl_seconds").GetInt32());
            return JsonResponse("""{ "access_token": "token", "expires_in": 120 }""");
        });

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "nova-3",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["deepgram"] = JsonSerializer.SerializeToElement(new { ttl_seconds = 120 })
            }
        });

        Assert.InRange(response.ExpiresAt, before + 120, before + 121);
    }

    private static DeepgramProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        => new(new ApiKeyResolver(), new HttpClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class ApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class HttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
