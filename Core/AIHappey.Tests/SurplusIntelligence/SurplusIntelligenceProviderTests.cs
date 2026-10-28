using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SurplusIntelligence;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.SurplusIntelligence;

public sealed class SurplusIntelligenceProviderTests
{
    [Fact]
    public async Task OpenAI_decisions_post_native_protocol_and_preserve_typed_questions()
    {
        var provider = CreateProvider("test-key", async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Assert.Equal("gpt-6-luna", root.GetProperty("model").GetString());
            Assert.Equal("evidence", root.GetProperty("input").GetString());
            Assert.Equal("predicate", root.GetProperty("questions")[0].GetProperty("type").GetString());
            Assert.Equal(JsonValueKind.True, root.GetProperty("questions")[1].GetProperty("choices")[0].GetProperty("value").ValueKind);
            Assert.Equal("score", root.GetProperty("questions")[2].GetProperty("type").GetString());
            return JsonResponse("""
                {
                  "model":"gpt-6-luna",
                  "answers":[
                    {"type":"predicate","name":"urgent","probability":0.82},
                    {"type":"choice","name":"queue","choice":true,"confidence":0.9,
                     "probabilities":[{"value":true,"probability":0.9},{"value":false,"probability":0.1}]},
                    {"type":"score","name":"severity","score":0.7,"confidence":0.8,
                     "probabilities":[{"value":0,"label":"low","probability":0.3},{"value":1,"label":"high","probability":0.7}]}
                  ],
                  "usage":{"input_tokens":412,"output_tokens":0,"total_tokens":412}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "surplusintelligence/gpt-6-luna",
            Input = "evidence",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "urgent", Instructions = "Is this urgent?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "queue",
                    Instructions = "Which queue?",
                    Choices = [new() { Value = true }, new() { Value = false }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "severity",
                    Instructions = "How severe?",
                    Levels = [new() { Label = "low" }, new() { Label = "high" }]
                }
            ]
        });

        Assert.Equal(0.82, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        Assert.Equal(JsonValueKind.True, Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]).Choice.Value.ValueKind);
        Assert.Equal(412, result.Usage.InputTokens);
    }

    [Fact]
    public async Task Jev_decisions_post_state_and_question_map()
    {
        var provider = CreateProvider("test-key", async (request, cancellationToken) =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            Assert.Equal("jev-1.13", root.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Object, root.GetProperty("state").ValueKind);
            Assert.Equal("noul", root.GetProperty("questions").GetProperty("urgent").GetProperty("type").GetString());
            Assert.Equal("choice", root.GetProperty("questions").GetProperty("queue").GetProperty("type").GetString());
            Assert.Equal("billing", root.GetProperty("questions").GetProperty("queue").GetProperty("criteria").EnumerateObject().First().Name);
            Assert.Equal(2, root.GetProperty("questions").GetProperty("severity").GetProperty("criteria").GetArrayLength());

            return JsonResponse("""
                {
                  "model":"jev-1.13",
                  "answers":{
                    "urgent":{"type":"noul","noul":0.82},
                    "queue":{"type":"choice","choice":"billing","confidence":0.9,
                      "probabilities":{"billing":0.9,"support":0.1}},
                    "severity":{"type":"score","score":0.7,"confidence":0.8,
                      "probabilities":{"0":0.3,"1":0.7}}
                  },
                  "usage":{"input_tokens":312,"output_tokens":0}
                }
                """);
        });

        var result = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "surplusintelligence/jev-1.13",
            State = JsonSerializer.SerializeToElement(new { text = "billing issue" }),
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is this urgent?" },
                ["queue"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payment issue", ["support"] = "General support" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe?",
                    Criteria = ["low", "high"]
                }
            }
        });

        Assert.Equal(0.82, Assert.IsType<DecisionBooleanAnswer>(result.Answers["urgent"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["queue"]).Choice);
        Assert.Equal(0.7, Assert.IsType<DecisionScoreAnswer>(result.Answers["severity"]).Score);
        Assert.Equal(312, result.Usage!.InputTokens);
    }

    [Fact]
    public async Task Models_map_documented_decision_type_and_capability_metadata()
    {
        var provider = CreateProvider(null, (_, _) => Task.FromResult(JsonResponse("""
            {
              "data":[
                {
                  "id":"gpt-6-luna","name":"GPT-6 Luna","context_length":128000,
                  "max_completion_tokens":8000,"pricing":{"prompt":"0.000001","completion":"0.000002"},
                  "architecture":{"input_modalities":["text","image"]},
                  "supported_features":["vision","reasoning"],
                  "supported_parameters":["temperature","tools"]
                },
                {"id":"jev-1.13","name":"JEV 1.13","type":"decision"}
              ]
            }
            """)));

        var models = (await provider.ListModels()).ToDictionary(static model => model.Id);

        Assert.Equal("decision", models["surplusintelligence/gpt-6-luna"].Type);
        Assert.Equal("decision", models["surplusintelligence/jev-1.13"].Type);
        Assert.Equal(128000, models["surplusintelligence/gpt-6-luna"].ContextWindow);
        Assert.Equal(8000, models["surplusintelligence/gpt-6-luna"].MaxTokens);
        Assert.Equal(0.000001m, models["surplusintelligence/gpt-6-luna"].Pricing!.Input);
        Assert.Contains("vision", models["surplusintelligence/gpt-6-luna"].Tags!);
        Assert.Contains("image", models["surplusintelligence/gpt-6-luna"].Tags!);
    }

    [Fact]
    public async Task Video_generation_tracks_the_native_operation_and_downloads_completed_content()
    {
        var provider = CreateProvider("test-key", async (request, cancellationToken) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return request.RequestUri!.AbsolutePath switch
            {
                "/v1/videos" => CreateJsonResponseForVideo(await request.Content!.ReadAsStringAsync(cancellationToken)),
                "/v1/videos/video-123" => JsonResponse("""{"id":"video-123","status":"completed"}"""),
                "/v1/videos/video-123/content" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent([1, 2, 3])
                },
                _ => throw new InvalidOperationException($"Unexpected path {request.RequestUri.AbsolutePath}")
            };
        });

        var started = await provider.StartVideoOperation(new VideoRequest
        {
            Model = "surplusintelligence/video-model",
            Prompt = "A short sunset"
        });
        var completed = Assert.IsType<VideoOperationCompletedResult>(
            await provider.GetVideoOperationStatus(started.Operation));

        var video = Assert.Single(completed.Videos);
        Assert.Equal("video/mp4", video.MediaType);
        Assert.Equal("AQID", video.Data);
    }

    [Fact]
    public async Task OpenAI_chat_responses_and_anthropic_messages_use_native_routes()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider("test-key", (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"error":{"message":"test error"}}""", Encoding.UTF8, "application/json")
            });
        });

        await Assert.ThrowsAnyAsync<Exception>(() => provider.CompleteChatAsync(new()
        {
            Model = "surplusintelligence/gpt-6-luna",
            Messages = []
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.ResponsesAsync(new()
        {
            Model = "surplusintelligence/gpt-6-luna",
            Input = "hello"
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.MessagesAsync(new()
        {
            Model = "surplusintelligence/claude-opus-4.6",
            MaxTokens = 16,
            Messages = []
        }, new() { ["anthropic-version"] = "2023-06-01" }));

        Assert.Equal(
            new[] { "/v1/chat/completions", "/v1/responses", "/v1/messages" },
            requests.Select(static request => request.RequestUri!.AbsolutePath));
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
        });
        Assert.Equal("2023-06-01", Assert.Single(requests[2].Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public void Header_and_scoped_provider_registrations_are_available()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SurplusIntelligence-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(
            new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("surplusintelligence"));

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor =>
            descriptor.ImplementationType == typeof(SurplusIntelligenceProvider));
        using var root = services.BuildServiceProvider();
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("surplusintelligence"));
    }

    private static SurplusIntelligenceProvider CreateProvider(
        string? key,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage CreateJsonResponseForVideo(string body)
    {
        using var document = JsonDocument.Parse(body);
        Assert.Equal("video-model", document.RootElement.GetProperty("model").GetString());
        Assert.Equal("A short sunset", document.RootElement.GetProperty("prompt").GetString());
        return JsonResponse("""{"id":"video-123","status":"queued"}""");
    }

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "surplusintelligence" ? key : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
