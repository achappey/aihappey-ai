using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Merge;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
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

            if (path.StartsWith("/v1/models", StringComparison.OrdinalIgnoreCase))
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

            if (path.StartsWith("/v1/routing/policies", StringComparison.OrdinalIgnoreCase))
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
        Assert.Empty(publicModel.Tags ?? []);

        var policyModel = Assert.Single(models, model => model.Id == "merge/routing-policy/rp_123");
        Assert.Equal("Cost optimized", policyModel.Name);
        Assert.Equal("language", policyModel.Type);
        Assert.Equal("Prefer lower cost models.", policyModel.Description);
        Assert.Equal("Merge", policyModel.OwnedBy);
        Assert.Empty(policyModel.Tags ?? []);
        Assert.Equal(2, models.Count);
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
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api-gateway.merge.dev/v1/openai/responses", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("application/json", request.Content?.Headers.ContentType?.MediaType);
            var body = await request.Content!.ReadAsStringAsync();
            capturedPayload = JsonDocument.Parse(body);

            // The selected OpenAI-compatible route uses Unix timestamps, unlike Merge's native route.
            return JsonResponse(OpenAICompatibleResponse);
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
        Assert.Equal("resp_1", response.Id);
        Assert.Equal(1767225600L, response.CreatedAt);
        Assert.Equal("completed", response.Status);
        var output = Assert.IsType<JsonElement>(Assert.Single(response.Output));
        Assert.Equal("Hello from Merge", output.GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal(1, response.NormalizedUsage?.InputTokens);
        Assert.Equal(1, response.NormalizedUsage?.OutputTokens);
        Assert.Equal(2, response.NormalizedUsage?.TotalTokens);
        Assert.NotNull(capturedPayload);

        using var captured = capturedPayload!;
        var root = captured.RootElement;
        Assert.False(root.TryGetProperty("model", out _));
        Assert.Equal("rp_123", root.GetProperty("routing_policy_id").GetString());
        Assert.True(root.GetProperty("include_routing_metadata").GetBoolean());
        Assert.Equal("openai", root.GetProperty("vendor").GetString());
        Assert.Equal(["openai", "azure"], root.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()).ToArray());
    }

    [Theory]
    [InlineData("merge/openai/gpt-5.1")]
    [InlineData("openai/gpt-5.1")]
    public async Task ResponsesAsync_uses_openai_compatible_route_for_explicit_models(string model)
    {
        var sends = 0;
        var provider = CreateProvider(async request =>
        {
            sends++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/openai/responses", request.RequestUri?.AbsolutePath);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.Equal("openai/gpt-5.1", root.GetProperty("model").GetString());
            Assert.False(root.TryGetProperty("routing_policy_id", out _));
            Assert.False(root.GetProperty("stream").GetBoolean());
            Assert.Equal("Hello", root.GetProperty("input")[0].GetProperty("content").GetString());
            Assert.Equal("azure", root.GetProperty("vendor").GetString());
            return JsonResponse(OpenAICompatibleResponse);
        });
        var request = CreateResponseRequest(model);
        request.Stream = false;
        request.Metadata = new() { ["merge"] = JsonSerializer.SerializeToElement(new { vendor = "azure" }) };

        var response = await provider.ResponsesAsync(request);

        Assert.Equal(1, sends);
        Assert.Equal("merge/openai/gpt-5.1", response.Model);
        Assert.Equal(1767225600L, response.CreatedAt);
        Assert.Equal(2, response.NormalizedUsage?.TotalTokens);
    }

    [Theory]
    [InlineData("merge/routing-policy/rp_123", true)]
    [InlineData("routing-policy/rp_123", true)]
    [InlineData("merge/openai/gpt-5.1", false)]
    [InlineData("openai/gpt-5.1", false)]
    public async Task ResponsesStreamingAsync_uses_openai_compatible_route_and_preserves_forwarding_and_events(string model, bool policy)
    {
        var sends = 0;
        var provider = CreateProvider(async request =>
        {
            sends++;
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api-gateway.merge.dev/v1/openai/responses", request.RequestUri?.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Contains(request.Headers.Accept, value => value.MediaType == "text/event-stream");
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = payload.RootElement;
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal("Hello", root.GetProperty("input")[0].GetProperty("content").GetString());
            if (policy)
            {
                Assert.False(root.TryGetProperty("model", out _));
                Assert.Equal("rp_123", root.GetProperty("routing_policy_id").GetString());
            }
            else
            {
                Assert.Equal("openai/gpt-5.1", root.GetProperty("model").GetString());
                Assert.False(root.TryGetProperty("routing_policy_id", out _));
            }
            Assert.True(root.GetProperty("include_routing_metadata").GetBoolean());
            Assert.Equal("openai", root.GetProperty("vendor").GetString());
            Assert.Equal(["openai", "azure"], root.GetProperty("vendors").EnumerateArray().Select(v => v.GetString()).ToArray());

            using var completedResponse = JsonDocument.Parse(OpenAICompatibleResponse);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "event: response.output_text.delta\n" +
                    "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"msg_1\",\"output_index\":0,\"content_index\":0,\"sequence_number\":1,\"delta\":\"Hello from Merge\"}\n\n" +
                    "event: response.completed\n" +
                    "data: {\"type\":\"response.completed\",\"sequence_number\":2,\"response\":" +
                    JsonSerializer.Serialize(completedResponse.RootElement) + "}\n\n" +
                    "data: [DONE]\n\n",
                    Encoding.UTF8, "text/event-stream")
            };
        });
        var request = CreateResponseRequest(model);
        request.Stream = true;
        request.Metadata = new() { ["merge"] = JsonSerializer.SerializeToElement(new
        {
            include_routing_metadata = true,
            vendor = "openai",
            vendors = new[] { "openai", "azure" }
        }) };
        var events = new List<ResponseStreamPart>();

        await foreach (var part in provider.ResponsesStreamingAsync(request))
            events.Add(part);

        Assert.Equal(1, sends);
        Assert.Collection(events,
            part =>
            {
                var delta = Assert.IsType<ResponseOutputTextDelta>(part);
                Assert.Equal("Hello from Merge", delta.Delta);
                Assert.Equal("msg_1", delta.ItemId);
                Assert.Equal(1, delta.SequenceNumber);
            },
            part =>
            {
                var completed = Assert.IsType<ResponseCompleted>(part);
                Assert.Equal(2, completed.SequenceNumber);
                Assert.Equal("resp_1", completed.Response.Id);
                Assert.Equal("merge/openai/gpt-5.1", completed.Response.Model);
                Assert.Equal(1767225600L, completed.Response.CreatedAt);
                Assert.Equal("completed", completed.Response.Status);
                Assert.Equal(1, completed.Response.NormalizedUsage?.InputTokens);
                Assert.Equal(1, completed.Response.NormalizedUsage?.OutputTokens);
                Assert.Equal(2, completed.Response.NormalizedUsage?.TotalTokens);
            });
    }

    private const string OpenAICompatibleResponse = """
        {
          "id": "resp_1",
          "object": "response",
          "created_at": 1767225600,
          "status": "completed",
          "model": "openai/gpt-5.1",
          "output": [
            {
              "id": "msg_1",
              "type": "message",
              "role": "assistant",
              "status": "completed",
              "content": [ { "type": "output_text", "text": "Hello from Merge", "annotations": [] } ]
            }
          ],
          "usage": { "input_tokens": 1, "output_tokens": 1, "total_tokens": 2 }
        }
        """;

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
