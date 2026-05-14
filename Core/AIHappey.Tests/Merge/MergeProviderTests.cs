using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Merge;
using AIHappey.Responses;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Merge;

public sealed class MergeProviderTests
{
    [Fact]
    public void GetIdentifier_returns_dedicated_merge_identifier()
    {
        var provider = CreateProvider(_ => JsonResponse("{}"));

        Assert.Equal("merge", provider.GetIdentifier());
    }

    [Fact]
    public async Task ListModels_returns_public_models_and_routing_policy_slugs()
    {
        var provider = CreateProvider(request =>
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;

            if (path.StartsWith("/models", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "model": "openai/gpt-5.1",
                      "provider": "openai",
                      "display_name": "GPT 5.1",
                      "availability_status": "available",
                      "vendors": {
                        "openai": {
                          "context_window": 1048576,
                          "max_output_tokens": 32768,
                          "availability_status": "available",
                          "capabilities": {
                            "input": ["text", "image", "document"],
                            "output": ["text", "tool_use"],
                            "supports_tool_calling": true,
                            "supports_tool_choice": true,
                            "supports_structured_outputs": true,
                            "streaming": true
                          },
                          "pricing": {
                            "currency": "USD",
                            "input_per_million": 1.25,
                            "output_per_million": 10.0
                          }
                        }
                      }
                    }
                  ],
                  "has_more": false,
                  "next_cursor": null
                }
                """);
            }

            if (path.StartsWith("/routing/policies", StringComparison.OrdinalIgnoreCase))
            {
                return JsonResponse("""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "rp_123",
                      "name": "Cost optimized",
                      "strategy": "cost_optimized",
                      "description": "Prefer lower cost models.",
                      "is_intelligent": true,
                      "is_active": true,
                      "providers": [
                        { "provider": "openai", "model": "gpt-5.1" }
                      ]
                    }
                  ]
                }
                """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var models = (await provider.ListModels()).ToList();

        var publicModel = Assert.Single(models, model => model.Id == "merge/openai/gpt-5.1");
        Assert.Equal("GPT 5.1", publicModel.Name);
        Assert.Equal("openai", publicModel.OwnedBy);
        Assert.Equal(1048576, publicModel.ContextWindow);
        Assert.Equal(32768, publicModel.MaxTokens);
        Assert.Equal("language", publicModel.Type);
        Assert.Equal(0.00000125m, publicModel.Pricing?.Input);
        Assert.Equal(0.000010m, publicModel.Pricing?.Output);
        Assert.Contains("vendor:openai", publicModel.Tags ?? []);
        Assert.Contains("input:image", publicModel.Tags ?? []);
        Assert.Contains("tools", publicModel.Tags ?? []);

        var policyModel = Assert.Single(models, model => model.Id == "merge/routing-policy/rp_123");
        Assert.Equal("Cost optimized", policyModel.Name);
        Assert.Equal("language", policyModel.Type);
        Assert.Equal("Prefer lower cost models.", policyModel.Description);
        Assert.Contains("routing-policy", policyModel.Tags ?? []);
        Assert.Contains("strategy:cost_optimized", policyModel.Tags ?? []);
        Assert.Contains("intelligent", policyModel.Tags ?? []);
        Assert.Contains("member:openai/gpt-5.1", policyModel.Tags ?? []);
    }

    [Fact]
    public void PrepareMergeResponseRequest_rewrites_explicit_merge_model_to_native_model()
    {
        var request = CreateResponseRequest("merge/openai/gpt-5.1");

        MergeProvider.PrepareMergeResponseRequest(request);

        Assert.Equal("openai/gpt-5.1", request.Model);
        Assert.False(request.AdditionalProperties?.ContainsKey("routing_policy_id") == true);
    }

    [Theory]
    [InlineData("merge/routing-policy/rp_123")]
    [InlineData("routing-policy/rp_123")]
    public void PrepareMergeResponseRequest_rewrites_routing_policy_slug_to_policy_id(string model)
    {
        var request = CreateResponseRequest(model);

        MergeProvider.PrepareMergeResponseRequest(request);

        Assert.Null(request.Model);
        Assert.Equal("rp_123", request.AdditionalProperties!["routing_policy_id"].GetString());
    }

    [Fact]
    public async Task ResponsesAsync_forwards_policy_and_provider_options_to_merge()
    {
        JsonDocument? capturedPayload = null;

        var provider = CreateProvider(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/responses")
            {
                var body = await request.Content!.ReadAsStringAsync();
                capturedPayload = JsonDocument.Parse(body);

                return JsonResponse("""
                {
                  "id": "resp_1",
                  "object": "response",
                  "created_at": "2026-01-01T00:00:00Z",
                  "model": "openai/gpt-5.1",
                  "output": [],
                  "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
                }
                """);
            }

            return JsonResponse("{}", HttpStatusCode.NotFound);
        });

        var request = CreateResponseRequest("merge/routing-policy/rp_123");
        request.Metadata = new Dictionary<string, object?>
        {
            ["merge"] = JsonSerializer.SerializeToElement(new
            {
                include_routing_metadata = true,
                vendor = "openai",
                vendors = new[] { "openai", "azure" }
            }, JsonSerializerOptions.Web)
        };

        var response = await provider.ResponsesAsync(request);

        Assert.Equal("merge/openai/gpt-5.1", response.Model);
        Assert.NotNull(capturedPayload);

        var root = capturedPayload!.RootElement;
        Assert.False(root.TryGetProperty("model", out _));
        Assert.Equal("rp_123", root.GetProperty("routing_policy_id").GetString());
        Assert.True(root.GetProperty("include_routing_metadata").GetBoolean());
        Assert.Equal("openai", root.GetProperty("vendor").GetString());
        Assert.Equal(["openai", "azure"], root.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    private static ResponseRequest CreateResponseRequest(string model)
        => new()
        {
            Model = model,
            Input = new ResponseInput(
            [
                new ResponseInputMessage
                {
                    Role = ResponseRole.User,
                    Content = new ResponseMessageContent("Hello")
                }
            ])
        };

    private static MergeProvider CreateProvider(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => CreateProvider(request => Task.FromResult(responder(request)));

    private static MergeProvider CreateProvider(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        var httpClient = new HttpClient(new StaticResponseHttpMessageHandler(responder));
        return new MergeProvider(
            new StaticApiKeyResolver(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(httpClient));
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class StaticApiKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "merge" ? "test-key" : null;
    }

    private sealed class StaticHttpClientFactory(HttpClient httpClient) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => httpClient;
    }

    private sealed class StaticResponseHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request);
    }
}
