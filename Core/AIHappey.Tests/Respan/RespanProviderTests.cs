using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Respan;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Respan;

public sealed class RespanProviderTests
{
    [Fact]
    public async Task Chat_responses_and_messages_use_the_documented_native_routes()
    {
        var requests = new List<HttpRequestMessage>();
        var provider = CreateProvider("respan-key", (request, _) =>
        {
            requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("test error")
            });
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new()
        {
            Model = "gpt-6-luna",
            Messages = []
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResponsesAsync(new()
        {
            Model = "gpt-6-luna",
            Input = "hello"
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.MessagesAsync(new()
        {
            Model = "claude-sonnet-5",
            MaxTokens = 16,
            Messages = []
        }, []));

        Assert.Equal(
            new[] { "/api/chat/completions", "/api/responses", "/api/anthropic/v1/messages" },
            requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("respan-key", request.Headers.Authorization.Parameter);
        });
        Assert.Equal("2023-06-01", Assert.Single(requests[2].Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task Chat_responses_and_messages_native_streams_are_parsed()
    {
        var provider = CreateProvider("stream-key", (request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/api/chat/completions" => SseResponse("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-6-luna","choices":[{"index":0,"delta":{"content":"chat"},"finish_reason":null}]}

                    data: [DONE]

                    """),
                "/api/responses" => SseResponse("""
                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"content_index":0,"delta":"response"}

                    data: [DONE]

                    """),
                "/api/anthropic/v1/messages" => SseResponse("""
                    event: message_start
                    data: {"type":"message_start","message":{"id":"msg-1","type":"message","role":"assistant","model":"claude-sonnet-5","content":[],"usage":{"input_tokens":2,"output_tokens":0}}}

                    event: content_block_delta
                    data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"message"}}

                    event: message_stop
                    data: {"type":"message_stop"}

                    """),
                _ => throw new InvalidOperationException($"Unexpected path {request.RequestUri.AbsolutePath}")
            });
        });

        var chat = new List<ChatCompletionUpdate>();
        await foreach (var update in provider.CompleteChatStreamingAsync(new()
                       {
                           Model = "gpt-6-luna",
                           Stream = true
                       }))
            chat.Add(update);
        Assert.Contains(chat, update => update.Model == "respan/gpt-6-luna");

        var responses = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new()
                       {
                           Model = "gpt-6-luna",
                           Input = "hello",
                           Stream = true
                       }))
            responses.Add(part);
        Assert.Contains(responses, part => part is AIHappey.Responses.Streaming.ResponseOutputTextDelta { Delta: "response" });

        var messages = new List<MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new()
                       {
                           Model = "claude-sonnet-5",
                           MaxTokens = 20,
                           Stream = true,
                           Messages = []
                       }, []))
            messages.Add(part);
        Assert.Contains(messages, part => part.Type == "content_block_delta" && part.Delta?.Text == "message");
        Assert.Contains(messages, part => part.Type == "message_stop");
    }

    [Fact]
    public async Task Decisions_send_supported_questions_images_reasoning_and_respan_parameters()
    {
        var provider = CreateProvider("decision-key", async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/api/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("decision-key", request.Headers.Authorization.Parameter);

            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            Assert.Equal("span-02", body.GetProperty("model").GetString());
            Assert.Equal(JsonValueKind.Array, body.GetProperty("state").ValueKind);
            Assert.Equal(
                "evidence",
                body.GetProperty("state")[0].GetProperty("content")[0].GetProperty("text").GetString());
            Assert.Equal(new[] { "question-0", "question-1", "question-2" },
                body.GetProperty("questions").EnumerateObject().Select(question => question.Name));
            Assert.Equal("noul", body.GetProperty("questions").GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal("choice", body.GetProperty("questions").GetProperty("question-1").GetProperty("type").GetString());
            Assert.Equal("option-0", body.GetProperty("questions").GetProperty("question-1").GetProperty("criteria").EnumerateObject().First().Name);
            Assert.Equal("score", body.GetProperty("questions").GetProperty("question-2").GetProperty("type").GetString());
            Assert.Equal("on", body.GetProperty("reasoning").GetProperty("mode").GetString());
            Assert.Equal(1500, body.GetProperty("reasoning").GetProperty("budget_ms").GetInt32());
            Assert.Equal("customer-1", body.GetProperty("respan_params").GetProperty("customer_identifier").GetString());
            Assert.Equal(1, body.GetProperty("images").GetArrayLength());

            return JsonResponse("""
                {
                  "model":"span-02",
                  "answers":{
                    "question-0":{"type":"noul","noul":0.93},
                    "question-1":{"type":"choice","choice":"option-1","confidence":0.9,
                      "probabilities":{"option-0":0.1,"option-1":0.9}},
                    "question-2":{"type":"score","score":1.5,"legend":{"0":"low","1":"medium","2":"high"},
                      "probabilities":{"0":0.1,"1":0.3,"2":0.6},"confidence":0.6}
                  },
                  "usage":{"input_tokens":12,"output_tokens":4}
                }
                """);
        });

        var result = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "respan/span-02",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "evidence" },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "valid", Instructions = "Is it valid?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "route",
                    Instructions = "Which team?",
                    Choices = [new() { Value = "billing" }, new() { Value = true }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "quality",
                    Instructions = "Rate quality.",
                    Levels = [new() { Label = "low" }, new() { Label = "medium" }, new() { Label = "high" }]
                }
            ],
            AdditionalProperties = new()
            {
                ["reasoning"] = JsonSerializer.SerializeToElement(new { mode = "on", budget_ms = 1500 }),
                ["respan_params"] = JsonSerializer.SerializeToElement(new { customer_identifier = "customer-1" })
            }
        });

        Assert.Equal(0.93, Assert.IsType<OpenAIDecisionPredicateAnswer>(result.Answers[0]).Probability);
        var choice = Assert.IsType<OpenAIDecisionChoiceAnswer>(result.Answers[1]);
        Assert.Equal(JsonValueKind.True, choice.Choice.Value.ValueKind);
        Assert.Equal(0.9, choice.Confidence);
        var score = Assert.IsType<OpenAIDecisionScoreAnswer>(result.Answers[2]);
        Assert.Equal(1.5, score.Score);
        Assert.Equal(new[] { "low", "medium", "high" }, score.Probabilities.Select(item => item.Label));
        Assert.Equal(16, result.Usage.TotalTokens);
    }

    [Fact]
    public async Task Vercel_decisions_map_to_native_question_ids_and_keep_response_metadata()
    {
        var provider = CreateProvider("decision-key", async (request, cancellationToken) =>
        {
            Assert.Equal("/api/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("trace", Assert.Single(request.Headers.GetValues("x-respan-trace")));
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            Assert.Equal(JsonValueKind.Object, body.GetProperty("state").ValueKind);
            Assert.Equal("noul", body.GetProperty("questions").GetProperty("question-0").GetProperty("type").GetString());
            Assert.Equal("choice", body.GetProperty("questions").GetProperty("question-1").GetProperty("type").GetString());
            Assert.Equal("score", body.GetProperty("questions").GetProperty("question-2").GetProperty("type").GetString());
            return JsonResponse("""
                {"model":"span-02","answers":{
                  "question-0":{"type":"noul","noul":0.81},
                  "question-1":{"type":"choice","choice":"option-1","confidence":0.9,
                    "probabilities":{"option-0":0.1,"option-1":0.9}},
                  "question-2":{"type":"score","score":0.5,"legend":{"0":"low","1":"high"},
                    "probabilities":{"0":0.25,"1":0.75},"confidence":0.75}},
                 "usage":{"input_tokens":10}}
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "respan/span-02",
            State = JsonSerializer.SerializeToElement(new { ticket = "refund" }),
            Headers = new() { ["Authorization"] = "attacker", ["x-api-key"] = "attacker", ["x-respan-trace"] = "trace" },
            ProviderOptions = new()
            {
                ["respan"] = JsonSerializer.SerializeToElement(new
                {
                    reasoning = new { mode = "off", budget_ms = 0 }
                })
            },
            Questions = new()
            {
                ["valid"] = new DecisionBooleanQuestion { Instructions = "Is this valid?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team?",
                    Criteria = new() { ["billing"] = "Payment question", ["support"] = "Other question" }
                },
                ["severity"] = new DecisionScoreQuestion { Instructions = "Rate it.", Criteria = ["low", "high"] }
            }
        });

        Assert.Equal(0.81, Assert.IsType<DecisionBooleanAnswer>(response.Answers["valid"]).Probability);
        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(response.Answers["team"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal(10, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("respan/span-02", response.Response!.ModelId);
        Assert.True(response.ProviderMetadata!.ContainsKey("respan"));
    }

    [Fact]
    public async Task Decisions_reject_unsupported_options_and_preserve_upstream_errors()
    {
        var noSend = CreateProvider("decision-key", (_, _) =>
            throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<ArgumentException>(() => noSend.OpenAIDecisionRequestAsync(new()
        {
            Model = "span-02",
            Input = "evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Is it valid?" }],
            AdditionalProperties = new() { ["reasoning"] = JsonSerializer.SerializeToElement(new { mode = "fast" }) }
        }));

        var manyImages = new OpenAIDecisionRequest
        {
            Model = "span-02",
            Input = Enumerable.Range(0, 5).Select(_ => new OpenAIDecisionInputMessage
            {
                Content = new OpenAIDecisionInputPart[]
                {
                    new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                }
            }).ToArray(),
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Check it." }]
        };
        await Assert.ThrowsAsync<ArgumentException>(() => noSend.OpenAIDecisionRequestAsync(manyImages));

        var failing = CreateProvider("decision-key", (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("quota exhausted")
        }));
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => failing.OpenAIDecisionRequestAsync(new()
        {
            Model = "span-02",
            Input = "evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Instructions = "Check it." }]
        }));
        Assert.Equal(HttpStatusCode.TooManyRequests, exception.StatusCode);
        Assert.Contains("quota exhausted", exception.Message);
    }

    [Fact]
    public async Task Model_catalog_authentication_and_registration_are_wired()
    {
        var model = Assert.Single(await new RespanProvider(new KeyResolver("key"), new ClientFactory(new HttpClient())).ListModels());
        Assert.Equal("respan/span-02", model.Id);
        Assert.Equal("decision", model.Type);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-Respan-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("RESPAN"));
        Assert.Equal("X-Respan-Key", HeaderApiKeyResolver.SupportedProviderHeaders["respan"]);

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(RespanProvider));
        using var root = services.BuildServiceProvider();
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("respan"));
    }

    private static RespanProvider CreateProvider(
        string? key,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string events)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(events, Encoding.UTF8, "text/event-stream")
        };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "respan" ? key : null;
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
