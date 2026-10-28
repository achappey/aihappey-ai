using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.RouterPlus;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.RouterPlus;

public sealed class RouterPlusProviderTests
{
    [Fact]
    public async Task Native_decisions_send_system_one_questions_and_map_typed_answers()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("https://api.routerplus.com/v1/decisions", request.RequestUri!.ToString());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            Assert.Equal("trace", Assert.Single(request.Headers.GetValues("x-routerplus-trace")));

            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("kev-4b", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("ticket text", body.RootElement.GetProperty("state").GetString());
            Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString());
            Assert.Equal("choice", body.RootElement.GetProperty("questions").GetProperty("questions").GetProperty("type").GetString());
            Assert.Equal("score", body.RootElement.GetProperty("questions").GetProperty("score").GetProperty("type").GetString());
            return JsonResponse("""
                {
                  "model":"kev-4b",
                  "answers":{
                    "urgent":{"type":"noul","noul":0.9},
                    "questions":{"type":"choice","choice":"only","probabilities":{"only":1},"confidence":1},
                    "score":{"type":"score","score":0.75,"legend":{"0":"low","1":"high"},
                      "probabilities":{"0":0.25,"1":0.75},"confidence":0.75}
                  },
                  "usage":{"input_tokens":12,"output_tokens":0}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "routerplus/kev-4b-20261028",
            State = "ticket text",
            Headers = new() { ["x-routerplus-trace"] = "trace", ["Authorization"] = "not forwarded" },
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is it urgent?" },
                ["questions"] = new DecisionChoiceQuestion
                {
                    Instructions = "Choose one.", Criteria = new() { ["only"] = null }
                },
                ["score"] = new DecisionScoreQuestion { Instructions = "Rate it.", Criteria = ["low", "high"] }
            }
        });

        Assert.Equal(0.9, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("only", Assert.IsType<DecisionChoiceAnswer>(result.Answers["questions"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(result.Answers["score"]).Score);
        Assert.Equal(12, result.Usage!.InputTokens);
        Assert.Equal("routerplus/kev-4b", result.Response!.ModelId);
        Assert.Equal("request-id", result.Response.Id);
    }

    [Fact]
    public async Task Decision_models_are_exposed_with_decision_catalog_type()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal("https://api.routerplus.com/v1/models", request.RequestUri!.ToString());
            return Task.FromResult(JsonResponse("""
                {"data":[
                  {"id":"gpt-4.1","owned_by":"openai"},
                  {"id":"video-model","type":"video"},
                  {"id":"routerplus/decider-2b","type":"language"}
                ]}
                """));
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["routerplus/decider-2b"].Type);
        Assert.Equal(25_600, models["routerplus/decider-2b"].ContextWindow);
        Assert.Equal(0m, models["routerplus/decider-2b"].Pricing!.Input);
        Assert.Equal("decision", models["routerplus/kev-4b"].Type);
        Assert.Equal(8_192, models["routerplus/kev-4b"].ContextWindow);
        Assert.Equal("language", models["routerplus/gpt-4.1"].Type);
        Assert.Equal("video", models["routerplus/video-model"].Type);
    }

    [Fact]
    public async Task OpenAI_image_generation_uses_native_documented_route()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.routerplus.com/v1/images/generations", request.RequestUri!.ToString());
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("image-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("draw a tree", body.RootElement.GetProperty("prompt").GetString());
            return JsonResponse("""{"created":1,"data":[{"b64_json":"aW1hZ2U="}]}""");
        });

        var response = await provider.OpenAIImageGenerationRequestAsync(new()
        {
            Model = "routerplus/image-model",
            Prompt = "draw a tree"
        });

        Assert.Equal("aW1hZ2U=", Assert.Single(response.Data!).B64Json);
    }

    [Fact]
    public async Task Decision_limits_and_unsupported_options_are_rejected_before_send()
    {
        var provider = CreateProvider((_, _) => throw new InvalidOperationException("Request should not be sent."));
        var request = new DecisionRequest
        {
            Model = "routerplus/decider-2b",
            State = "state",
            Questions = new()
            {
                ["choice"] = new DecisionChoiceQuestion
                {
                    Instructions = "choose", Criteria = Enumerable.Range(0, 256)
                        .ToDictionary(index => index.ToString(), _ => (DecisionInput?)null)
                }
            }
        };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request.Questions["choice"] = new DecisionBooleanQuestion { Instructions = "yes?" };
        request.ProviderOptions = new() { ["routerplus"] = JsonSerializer.SerializeToElement(new { cache = false }) };
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));

        request.ProviderOptions = null;
        request.Model = "routerplus/unknown-decision";
        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(request));
    }

    private static RouterPlusProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new TestKeyResolver(), new TestHttpClientFactory(new HttpClient(new TestHandler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string body)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        response.Headers.Add("x-request-id", "request-id");
        return response;
    }

    private sealed class TestKeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => "test-key";
    }

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
