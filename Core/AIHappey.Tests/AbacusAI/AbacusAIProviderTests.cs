using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.AbacusAI;
using AIHappey.HeaderAuth;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.AbacusAI;

public sealed class AbacusAIProviderTests
{
    [Fact]
    public async Task Chat_responses_and_anthropic_messages_use_their_native_routes()
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

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new()
        {
            Model = "gpt-6-astra",
            Messages = []
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResponsesAsync(new()
        {
            Model = "gpt-6-astra",
            Input = "hello"
        }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.MessagesAsync(new()
        {
            Model = "claude-sonnet-5",
            MaxTokens = 16,
            Messages = []
        }, new() { ["anthropic-version"] = "2023-06-01" }));

        Assert.Equal(
            new[] { "/v1/chat/completions", "/v1/responses", "/v1/messages" },
            requests.Select(request => request.RequestUri!.AbsolutePath));
        Assert.All(requests, request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization.Parameter);
        });
        Assert.Equal("2023-06-01", Assert.Single(requests[2].Headers.GetValues("anthropic-version")));
    }

    [Fact]
    public async Task Native_chat_responses_and_messages_streams_are_parsed()
    {
        var provider = CreateProvider("stream-key", (request, _) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            return Task.FromResult(request.RequestUri!.AbsolutePath switch
            {
                "/v1/chat/completions" => SseResponse("""
                    data: {"id":"chat-1","object":"chat.completion.chunk","created":1,"model":"gpt-6-astra","choices":[{"index":0,"delta":{"content":"chat"},"finish_reason":null}]}

                    data: [DONE]

                    """),
                "/v1/responses" => SseResponse("""
                    event: response.output_text.delta
                    data: {"type":"response.output_text.delta","item_id":"msg-1","output_index":0,"content_index":0,"delta":"response"}

                    data: [DONE]

                    """),
                "/v1/messages" => SseResponse("""
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
                           Model = "gpt-6-astra",
                           Stream = true
                       }))
            chat.Add(update);
        Assert.Contains(chat, update => update.Model == "abacusai/gpt-6-astra");

        var responses = new List<AIHappey.Responses.Streaming.ResponseStreamPart>();
        await foreach (var part in provider.ResponsesStreamingAsync(new()
                       {
                           Model = "gpt-6-astra",
                           Input = "hello",
                           Stream = true
                       }))
            responses.Add(part);
        Assert.Contains(responses, part => part is ResponseOutputTextDelta { Delta: "response" });

        var messages = new List<AIHappey.Messages.MessageStreamPart>();
        await foreach (var part in provider.MessagesStreamingAsync(new()
                       {
                           Model = "claude-sonnet-5",
                           MaxTokens = 20,
                           Stream = true,
                           Messages = []
                       }, new() { ["anthropic-version"] = "2023-06-01" }))
            messages.Add(part);
        Assert.Contains(messages, part => part.Type == "content_block_delta" && part.Delta?.Text == "message");
        Assert.Contains(messages, part => part.Type == "message_stop");
    }

    [Fact]
    public async Task Native_decisions_send_each_question_type_and_preserve_image_input()
    {
        var provider = CreateProvider("decision-key", async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("decision-key", request.Headers.Authorization.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            Assert.Equal("gpt-6-luna", body.GetProperty("model").GetString());
            Assert.Equal("input_image", body.GetProperty("input")[0].GetProperty("content")[1].GetProperty("type").GetString());
            Assert.Equal("predicate", body.GetProperty("questions")[0].GetProperty("type").GetString());
            Assert.Equal("choice", body.GetProperty("questions")[1].GetProperty("type").GetString());
            Assert.Equal("score", body.GetProperty("questions")[2].GetProperty("type").GetString());

            return JsonResponse("""
                {
                  "model":"gpt-6-luna",
                  "answers":[
                    {"type":"predicate","name":"damaged","probability":0.97},
                    {"type":"choice","name":"team","choice":"technical","confidence":0.95,
                     "probabilities":[{"value":"billing","probability":0.05},{"value":"technical","probability":0.95}]},
                    {"type":"score","name":"severity","score":1.6,"confidence":0.7,
                     "probabilities":[{"value":0,"label":"minor","probability":0.1},{"value":1,"label":"moderate","probability":0.2},{"value":2,"label":"severe","probability":0.7}]}
                  ],
                  "usage":{"input_tokens":284}
                }
                """);
        });

        var response = await provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "abacusai/gpt-6-luna",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputText { Text = "Inspect the returned item." },
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Name = "damaged", Instructions = "Is it damaged?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "team",
                    Instructions = "Which team should handle it?",
                    Choices = [new() { Value = "billing" }, new() { Value = "technical" }]
                },
                new OpenAIDecisionScoreQuestion
                {
                    Name = "severity",
                    Instructions = "How severe is it?",
                    Levels = [new() { Label = "minor" }, new() { Label = "moderate" }, new() { Label = "severe" }]
                }
            ]
        });

        Assert.Equal(0.97, Assert.IsType<OpenAIDecisionPredicateAnswer>(response.Answers[0]).Probability);
        Assert.Equal("technical", Assert.IsType<OpenAIDecisionChoiceAnswer>(response.Answers[1]).Choice.Value.GetString());
        Assert.Equal(1.6, Assert.IsType<OpenAIDecisionScoreAnswer>(response.Answers[2]).Score);
        Assert.Equal(284, response.Usage.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal(284, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Vercel_decisions_use_the_native_endpoint_and_map_typed_answers()
    {
        var provider = CreateProvider("decision-key", async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("jev-latest", document.RootElement.GetProperty("model").GetString());
            Assert.Equal("predicate", document.RootElement.GetProperty("questions")[0].GetProperty("type").GetString());
            Assert.Equal("choice", document.RootElement.GetProperty("questions")[1].GetProperty("type").GetString());
            Assert.Equal("score", document.RootElement.GetProperty("questions")[2].GetProperty("type").GetString());
            return JsonResponse("""
                {"model":"jev-latest","answers":[
                  {"type":"predicate","name":"urgent","probability":0.81},
                  {"type":"choice","name":"department","choice":"technical","confidence":0.9,
                   "probabilities":[{"value":"billing","probability":0.1},{"value":"technical","probability":0.9}]},
                  {"type":"score","name":"severity","score":0.75,"confidence":0.75,
                   "probabilities":[{"value":0,"label":"0","probability":0.25},{"value":1,"label":"1","probability":0.75}]}
                ],
                 "usage":{"input_tokens":10}}
                """);
        });

        var response = await provider.DecisionRequestAsync(new()
        {
            Model = "abacusai/jev-latest",
            State = "Customer reports a service outage.",
            Questions = new()
            {
                ["urgent"] = new DecisionBooleanQuestion { Instructions = "Is this urgent?" },
                ["department"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle this?",
                    Criteria = new() { ["billing"] = "Payment issue", ["technical"] = "Service failure" }
                },
                ["severity"] = new DecisionScoreQuestion
                {
                    Instructions = "How severe is the issue?",
                    Criteria = ["minor", "severe"]
                }
            }
        });

        Assert.Equal(0.81, Assert.IsType<DecisionBooleanAnswer>(response.Answers["urgent"]).Probability);
        Assert.Equal("technical", Assert.IsType<DecisionChoiceAnswer>(response.Answers["department"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(response.Answers["severity"]).Score);
        Assert.Equal(10, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal("abacusai/jev-latest", response.Response!.ModelId);
    }

    [Fact]
    public async Task Decisions_reject_unsupported_models_and_jev_images_before_sending()
    {
        var provider = CreateProvider("decision-key", (_, _) =>
            throw new InvalidOperationException("Request should not be sent."));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "gpt-6-luna-thinking",
            Input = "evidence",
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "Check it." }]
        }));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.OpenAIDecisionRequestAsync(new()
        {
            Model = "jev-latest",
            Input = new OpenAIDecisionInputMessage[]
            {
                new()
                {
                    Content = new OpenAIDecisionInputPart[]
                    {
                        new OpenAIDecisionInputImage { ImageUrl = "data:image/png;base64,AQ==" }
                    }
                }
            },
            Questions = [new OpenAIDecisionPredicateQuestion { Name = "flag", Instructions = "Check it." }]
        }));
    }

    [Fact]
    public async Task Model_discovery_marks_only_documented_decision_models_and_includes_static_entries()
    {
        var provider = CreateProvider("model-key", async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return JsonResponse("""
                {"object":"list","data":[
                  {"id":"gpt-6-luna","owned_by":"OpenAI"},
                  {"id":"gpt-6-astra","owned_by":"Abacus.AI"}
                ]}
                """);
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);
        Assert.Equal("decision", models["abacusai/gpt-6-luna"].Type);
        Assert.Equal("decision", models["abacusai/jev-latest"].Type);
        Assert.Equal("language", models["abacusai/gpt-6-astra"].Type);
        Assert.Equal("OpenAI", models["abacusai/gpt-6-luna"].OwnedBy);
    }

    [Fact]
    public void Header_and_scoped_provider_registrations_are_available()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-AbacusAI-Key"] = "  test-key  ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("ABACUSAI"));
        Assert.Equal("X-AbacusAI-Key", HeaderApiKeyResolver.SupportedProviderHeaders["abacusai"]);

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(AbacusAIProvider));
        using var root = services.BuildServiceProvider();
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("abacusai"));
    }

    private static AbacusAIProvider CreateProvider(
        string? key,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())));

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
        public string? Resolve(string provider) => provider == "abacusai" ? key : null;
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
