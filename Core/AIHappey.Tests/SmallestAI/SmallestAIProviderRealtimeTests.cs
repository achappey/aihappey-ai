using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.SmallestAI;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.SmallestAI;

public sealed class SmallestAIProviderRealtimeTests
{
    [Fact]
    public async Task ListModels_adds_active_atoms_agents_as_audio_models()
    {
        var provider = CreateProvider(request => request.RequestUri?.AbsolutePath switch
        {
            "/waves/v1/lightning-v3.1/get_voices" => JsonResponse("""{ "voices": [] }"""),
            "/atoms/v1/agent" => JsonResponse(
                """
                {
                  "status": true,
                  "data": {
                    "total": 3,
                    "agents": [
                      { "_id": "agent-1", "name": "Receptionist", "description": "Answers calls", "workflowType": "single_prompt", "slmModel": "electron" },
                      { "_id": "agent-2", "name": "Archived", "archived": true },
                      { "name": "Missing id" }
                    ]
                  }
                }
                """),
            _ => JsonResponse("{}", HttpStatusCode.NotFound)
        });

        var models = (await provider.ListModels()).ToList();

        var model = Assert.Single(models, model => model.Id == "smallestai/agent-1");
        Assert.Equal("audio", model.Type);
        Assert.Equal("Atoms / Receptionist", model.Name);
        Assert.Equal("Answers calls", model.Description);
        Assert.DoesNotContain(models, model => model.Id == "smallestai/agent-2");
    }

    [Fact]
    public async Task GetRealtimeToken_registers_model_agent_as_webcall_and_forwards_only_variables()
    {
        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath != "/atoms/v1/conversation/register-call")
                return JsonResponse("{}", HttpStatusCode.NotFound);

            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = document.RootElement;
            Assert.Equal("agent-123", root.GetProperty("agent_id").GetString());
            Assert.Equal("webcall", root.GetProperty("mode").GetString());
            Assert.Equal("Ada", root.GetProperty("variables").GetProperty("name").GetString());
            Assert.Equal(2, root.GetProperty("variables").GetProperty("attempt").GetInt32());
            Assert.True(root.GetProperty("variables").GetProperty("enabled").GetBoolean());
            Assert.False(root.TryGetProperty("ignored", out _));

            return JsonResponse(
                """
                {
                  "status": true,
                  "data": {
                    "access_token": "wct_abc+123",
                    "expires_in": 30,
                    "sample_rate": 24000
                  }
                }
                """,
                HttpStatusCode.Created);
        });

        var before = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var response = await provider.GetRealtimeToken(new RealtimeRequest
        {
            Model = "smallestai/agent-123",
            ProviderOptions = new Dictionary<string, JsonElement>
            {
                ["smallestai"] = JsonSerializer.SerializeToElement(new
                {
                    variables = new { name = "Ada", attempt = 2, enabled = true },
                    ignored = "not forwarded"
                })
            }
        });

        Assert.Equal("wct_abc+123", response.Value);
        Assert.InRange(response.ExpiresAt, before + 30, before + 31);
        Assert.True(response.ProviderMetadata?.ContainsKey("smallestai") == true);
        var metadata = response.ProviderMetadata!["smallestai"];
        Assert.Equal(
            "wss://api.smallest.ai/atoms/v1/agent/connect?token=wct_abc%2B123",
            metadata.GetProperty("websocket_url").GetString());
        Assert.Equal(24000, metadata.GetProperty("sample_rate").GetInt32());
    }

    [Fact]
    public async Task GetRealtimeToken_rejects_a_success_response_without_access_token()
    {
        var provider = CreateProvider(request => JsonResponse(
            """{ "status": true, "data": { "expires_in": 30 } }""",
            HttpStatusCode.Created));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            new RealtimeRequest { Model = "agent-123" }));

        Assert.Contains("access token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRealtimeToken_surfaces_registration_failure_body()
    {
        var provider = CreateProvider(request => JsonResponse(
            """{ "status": false, "errors": ["no credits"] }""",
            HttpStatusCode.BadRequest));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(
            new RealtimeRequest { Model = "agent-123" }));

        Assert.Contains("no credits", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("smallestai/lightning_v3.1/meher")]
    [InlineData("smallestai/lightning_v3.1_pro")]
    [InlineData("smallestai/pulse-2")]
    public async Task Waves_tokens_use_console_endpoint_and_preserve_contract(string model)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5);
        var calls = 0;
        var provider = CreateProvider(async request =>
        {
            calls++;
            Assert.Equal("https://api.smallest.ai/console/v1/auth/token", request.RequestUri!.ToString());
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(300, body.RootElement.GetProperty("ttl_seconds").GetInt32());
            return JsonResponse(JsonSerializer.Serialize(new { access_token = "wat_short_lived", token_type = "Bearer", expires_in = 300, expires_at = expiry }), HttpStatusCode.Created);
        });
        var token = await provider.GetRealtimeToken(new() { Model = model });
        Assert.Equal(1, calls);
        Assert.Equal("wat_short_lived", token.Value);
        Assert.Equal(expiry.ToUnixTimeSeconds(), token.ExpiresAt);
        Assert.Contains("/live", token.ProviderMetadata!["smallestai"].GetProperty("websocket_url").GetString());
    }

    [Fact]
    public async Task Waves_authentication_failure_has_no_fallback_or_secret_body()
    {
        var calls = 0;
        var provider = CreateProvider(request => { calls++; return JsonResponse("secret-body", HttpStatusCode.Unauthorized); });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetRealtimeToken(new() { Model = "smallestai/pulse" }));
        Assert.Equal(1, calls);
        Assert.Contains("401", error.Message);
        Assert.DoesNotContain("secret-body", error.Message);
    }

    private static SmallestAIProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var client = new HttpClient(new StaticResponseHttpMessageHandler(responder));

        return new SmallestAIProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(client));
    }

    private static SmallestAIProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "smallestai" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request);
    }
}
