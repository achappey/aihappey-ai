using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.MeraGPT;
using AIHappey.HeaderAuth;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.MeraGPT;

public sealed class MeraGPTProviderTests
{
    [Fact]
    public async Task Model_catalog_lists_documented_models_and_types_decider_as_decision()
    {
        var models = (await CreateProvider(NoNetwork).ListModels()).ToDictionary(model => model.Id);

        Assert.Equal(3, models.Count);
        Assert.Equal("decision", models["meragpt/decider-1"].Type);
        Assert.Equal("language", models["meragpt/restyler-1"].Type);
        Assert.Equal("language", models["meragpt/fanout-1"].Type);
    }

    [Fact]
    public async Task Native_decisions_map_question_types_probabilities_and_usage()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://meragpt.com/v1/systemone", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("decider-1", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("Ticket needs refund.", body.RootElement.GetProperty("state").GetString());

            var questions = body.RootElement.GetProperty("questions");
            var refund = questions.GetProperty("refund_requested");
            Assert.Equal("noul", refund.GetProperty("type").GetString());
            Assert.Equal("money back", refund.GetProperty("criteria").GetProperty("true").GetString());
            Assert.Equal("choice", questions.GetProperty("team").GetProperty("type").GetString());
            Assert.Equal("Charges and refunds.", questions.GetProperty("team").GetProperty("criteria").GetProperty("billing").GetString());
            Assert.Equal("score", questions.GetProperty("urgency").GetProperty("type").GetString());
            Assert.Equal(new[] { "Can wait", "Today" },
                questions.GetProperty("urgency").GetProperty("criteria").EnumerateArray().Select(value => value.GetString()));

            return Json("""
                {"id":"req-1","object":"systemone","model":"decider-1","answers":{
                  "refund_requested":{"type":"noul","noul":0.91},
                  "team":{"type":"choice","choice":"billing","confidence":0.8,
                    "probabilities":{"billing":0.8,"support":0.2}},
                  "urgency":{"type":"score","score":0.75,"confidence":0.75,
                    "legend":{"0":"Can wait","1":"Today"},
                    "probabilities":{"0":0.25,"1":0.75}}},
                 "usage":{"input_tokens":20,"output_tokens":0,"cost_usd":"$0.00001"}}
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "meragpt/decider-1",
            State = "Ticket needs refund.",
            Questions = new()
            {
                ["refund_requested"] = new DecisionBooleanQuestion
                {
                    Instructions = "Is the customer asking for a refund?",
                    Criteria = new() { True = "money back", False = "not money back" }
                },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this?",
                    Criteria = new()
                    {
                        ["billing"] = "Charges and refunds.",
                        ["support"] = "Product usage."
                    }
                },
                ["urgency"] = new DecisionScoreQuestion
                {
                    Instructions = "How urgent is this?",
                    Criteria = ["Can wait", "Today"]
                }
            }
        });

        Assert.Equal(0.91, Assert.IsType<DecisionBooleanAnswer>(result.Answers["refund_requested"]).Probability);
        Assert.Equal("billing", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(result.Answers["urgency"]).Score);
        Assert.Equal(20, result.Usage!.InputTokens);
        Assert.Equal("meragpt/decider-1", result.Response!.ModelId);
    }

    [Fact]
    public async Task OpenAI_decisions_use_the_native_systemone_contract()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/systemone", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("A billing ticket.", body.RootElement.GetProperty("state").GetString());
            Assert.Equal("noul", body.RootElement.GetProperty("questions").GetProperty("billing").GetProperty("type").GetString());
            return Json("""
                {"id":"req-openai","object":"systemone","model":"decider-1",
                 "answers":{"billing":{"type":"noul","noul":0.97}},
                 "usage":{"input_tokens":8,"output_tokens":0,"cost_usd":"$0.00001"}}
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "meragpt/decider-1",
            Input = "A billing ticket.",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion
                {
                    Name = "billing",
                    Instructions = "Is this about billing?"
                }
            ]
        });

        Assert.Equal(0.97, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers.Single()).Probability);
        Assert.Equal(8, result.Usage.InputTokens);
        Assert.True(result.AdditionalProperties!.ContainsKey("providerMetadata"));
    }

    [Fact]
    public async Task Fanout_uses_native_chat_completions_and_preserves_structured_metadata()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("https://meragpt.com/v1/chat/completions", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("fanout-1", body.RootElement.GetProperty("model").GetString());
            return Json("""
                {"id":"chat-1","object":"chat.completion","created":1,"model":"fanout-1",
                 "choices":[{"index":0,"message":{"role":"assistant","content":"project tools 2026\nproject management pricing"},"finish_reason":"stop"}],
                 "usage":{"prompt_tokens":12,"completion_tokens":9,"total_tokens":21},
                 "meragpt":{"queries":["project tools 2026","project management pricing"]}}
                """);
        });

        var result = await provider.CompleteChatAsync(new ChatCompletionOptions
        {
            Model = "fanout-1",
            Messages = [new() { Role = "user", Content = JsonSerializer.SerializeToElement("What are project tools?") }]
        });

        Assert.Equal("meragpt/fanout-1", result.Model);
        Assert.Equal(2, result.AdditionalProperties!["meragpt"].GetProperty("queries").GetArrayLength());
        var responsesResult = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "meragpt/fanout-1",
            Input = "What are project tools?"
        });
        Assert.Contains("project management pricing", JsonSerializer.Serialize(responsesResult));
        await Assert.ThrowsAsync<NotSupportedException>(async () =>
        {
            await foreach (var _ in provider.CompleteChatStreamingAsync(new ChatCompletionOptions { Model = "fanout-1" })) { }
        });
    }

    [Fact]
    public async Task Restyler_is_available_through_responses_and_vercel_streaming_as_text()
    {
        var calls = 0;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            calls++;
            Assert.Equal("https://meragpt.com/v1/restyle", request.RequestUri!.AbsoluteUri);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("restyler-1", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("A sufficiently long source document.", body.RootElement.GetProperty("text").GetString());
            return Json("""
                {"id":"req-restyle","object":"restyle","model":"restyler-1",
                 "text":"A polished document.","blocks":[{"index":0,"words":5,"status":"rewritten"}],
                 "rewritten":1,"total":1,
                 "usage":{"input_tokens":10,"output_tokens":8,"total_tokens":18,"cost_usd":"$0.00001"}}
                """);
        });

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new ResponseRequest
        {
            Model = "meragpt/restyler-1",
            Instructions = "Use a different style.",
            Input = "A sufficiently long source document."
        }));
        Assert.Equal(0, calls);

        var response = await provider.ResponsesAsync(new ResponseRequest
        {
            Model = "meragpt/restyler-1",
            Input = "A sufficiently long source document."
        });
        Assert.Contains("A polished document.", JsonSerializer.Serialize(response));
        Assert.Contains("blocks", JsonSerializer.Serialize(response));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new ResponseRequest
        {
            Model = "meragpt/fanout-1",
            Input = "A buyer question.",
            TopP = 0.5
        }));
        Assert.Equal(1, calls);

        var streamedParts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(new ChatRequest
        {
            Id = "restyle-chat",
            Model = "meragpt/restyler-1",
            Messages =
            [
                new UIMessage
                {
                    Id = "user-1",
                    Role = Role.user,
                    Parts = [new TextUIPart { Text = "A sufficiently long source document." }]
                }
            ]
        }))
            streamedParts.Add(part);

        Assert.Contains(streamedParts, part => part is TextDeltaUIMessageStreamPart
        {
            Delta: "A polished document."
        });
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Registration_and_api_key_header_are_wired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MeraGPT-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(
            new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("MERAGPT"));
        Assert.Equal("X-MeraGPT-Key", HeaderApiKeyResolver.SupportedProviderHeaders["meragpt"]);

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(MeraGPTProvider));
    }

    private static MeraGPTProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static Task<HttpResponseMessage> NoNetwork(HttpRequestMessage _, CancellationToken __)
        => throw new InvalidOperationException("No network request was expected.");

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "meragpt" ? "test-key" : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
