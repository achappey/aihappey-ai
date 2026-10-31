using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.MindsHub;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.MindsHub;

public sealed class MindsHubProviderTests
{
    [Fact]
    public async Task ListModels_maps_catalog_kinds_and_metadata()
    {
        var provider = CreateProvider((request, _) =>
        {
            Assert.Equal("https://api.mindshub.ai/v1/models", request.RequestUri!.AbsoluteUri);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            return Task.FromResult(JsonResponse("""
                {"object":"list","data":[
                  {"id":"jev","name":"Jev","kind":"decision","owned_by":"TypeSafe","context_window":64000},
                  {"id":"text-embedding","kind":"embedding"},
                  {"id":"chat-model","kind":"language","description":"A chat model"}]}
                """));
        });

        var models = (await provider.ListModels()).ToDictionary(model => model.Id);

        Assert.Equal("decision", models["mindshub/jev"].Type);
        Assert.Equal("Jev", models["mindshub/jev"].Name);
        Assert.Equal(64000, models["mindshub/jev"].ContextWindow);
        Assert.Equal("TypeSafe", models["mindshub/jev"].OwnedBy);
        Assert.Equal("embedding", models["mindshub/text-embedding"].Type);
        Assert.Equal("language", models["mindshub/chat-model"].Type);
    }

    [Theory]
    [InlineData("""{"id":"minimal"}""")]
    [InlineData("""{"id":"minimal","kind":null,"type":null,"name":null,"owned_by":null,"provider":null,"description":null}""")]
    public async Task ListModels_allows_missing_or_null_optional_metadata(string modelJson)
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("{\"data\":[" + modelJson + "]}")));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("mindshub/minimal", model.Id);
        Assert.Equal("minimal", model.Name);
        Assert.Equal("MindsHub", model.OwnedBy);
        Assert.Equal("language", model.Type);
        Assert.Null(model.Description);
    }

    [Fact]
    public async Task ListModels_falls_back_to_type_and_provider_metadata()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse("""
            {"data":[{"id":"embedding-model","kind":null,"type":"embedding",
                      "owned_by":null,"provider":"Model owner","description":"Embedding model"}]}
            """)));

        var model = Assert.Single(await provider.ListModels());

        Assert.Equal("embedding", model.Type);
        Assert.Equal("Model owner", model.OwnedBy);
        Assert.Equal("Embedding model", model.Description);
    }

    [Theory]
    [InlineData("id")]
    [InlineData("kind")]
    [InlineData("type")]
    [InlineData("name")]
    [InlineData("owned_by")]
    [InlineData("provider")]
    [InlineData("description")]
    public async Task ListModels_still_rejects_non_string_metadata(string property)
    {
        var item = new Dictionary<string, object?> { ["id"] = "model", [property] = 123 };
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { data = new[] { item } }))));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ListModels());

        Assert.IsType<JsonException>(exception.InnerException);
    }

    [Theory]
    [InlineData("""{"noul":0.8}""")]
    [InlineData("""{"type":null,"noul":0.8}""")]
    [InlineData("""{"type":123,"noul":0.8}""")]
    public async Task Decisions_still_require_a_string_answer_type(string answerJson)
    {
        var body = "{\"model\":\"jev\",\"answers\":{\"valid\":" + answerJson
            + "},\"usage\":{\"input_tokens\":1,\"output_tokens\":0}}";
        var provider = CreateProvider((_, _) => Task.FromResult(JsonResponse(body)));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "jev",
            State = "Ticket state",
            Questions = new() { ["valid"] = new DecisionBooleanQuestion { Instructions = "Is it valid?" } }
        }));

        Assert.IsType<JsonException>(exception.InnerException);
        Assert.Contains("type", exception.InnerException.Message);
    }

    [Fact]
    public async Task Decisions_forward_native_state_questions_and_usage()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/decisions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var document = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var body = document.RootElement;
            Assert.Equal("jev", body.GetProperty("model").GetString());
            Assert.Equal("order-123", body.GetProperty("state").GetProperty("order_id").GetString());
            var questions = body.GetProperty("questions");
            Assert.Equal("noul", questions.GetProperty("replacement").GetProperty("type").GetString());
            Assert.Equal("choice", questions.GetProperty("team").GetProperty("type").GetString());
            Assert.Equal("score", questions.GetProperty("risk").GetProperty("type").GetString());
            return JsonResponse("""
                {"model":"jev-1.13.0","answers":{
                  "replacement":{"type":"noul","noul":0.07},
                  "team":{"type":"choice","choice":"support","confidence":0.9,
                    "probabilities":{"support":0.9,"sales":0.1}},
                  "risk":{"type":"score","score":0.75,"confidence":0.8,
                    "legend":{"0":"low","1":"high"},"probabilities":{"0":0.25,"1":0.75}}},
                 "usage":{"input_tokens":120,"output_tokens":16}}
                """);
        });

        var result = await provider.DecisionRequestAsync(new()
        {
            Model = "mindshub/jev",
            State = JsonSerializer.SerializeToElement(new { order_id = "order-123" }),
            Questions = new()
            {
                ["replacement"] = new DecisionBooleanQuestion { Instructions = "Is replacement requested?" },
                ["team"] = new DecisionChoiceQuestion
                {
                    Instructions = "Which team should handle it?",
                    Criteria = new() { ["support"] = "Customer support", ["sales"] = "Sales" }
                },
                ["risk"] = new DecisionScoreQuestion
                {
                    Instructions = "What is the risk?",
                    Criteria = ["low", "high"]
                }
            }
        });

        Assert.Equal(0.07, Assert.IsType<DecisionBooleanAnswer>(result.Answers["replacement"]).Probability);
        Assert.Equal("support", Assert.IsType<DecisionChoiceAnswer>(result.Answers["team"]).Choice);
        Assert.Equal(0.75, Assert.IsType<DecisionScoreAnswer>(result.Answers["risk"]).Score);
        Assert.Equal(120, result.Usage!.InputTokens);
        Assert.Equal(16, result.Usage.OutputTokens);
        Assert.Equal("mindshub/jev-1.13.0", result.Response!.ModelId);
    }

    [Fact]
    public async Task Decisions_preserve_http_status_body_and_recovery_header()
    {
        var provider = CreateProvider((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.PaymentRequired)
        {
            Headers = { { "X-MindsHub-Recovery-Url", "/billing" } },
            Content = new StringContent("{\"error\":{\"code\":\"wallet_empty\"}}")
        }));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "mindshub/jev",
            State = "ticket",
            Questions = new()
            {
                ["valid"] = new DecisionBooleanQuestion { Instructions = "Is the ticket valid?" }
            }
        }));

        Assert.Equal(HttpStatusCode.PaymentRequired, exception.StatusCode);
        Assert.Contains("wallet_empty", exception.Message);
        Assert.Contains("/billing", exception.Message);
    }

    [Fact]
    public async Task Chat_completions_and_embeddings_use_native_openai_compatible_routes()
    {
        var calls = 0;
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            if (request.RequestUri!.AbsolutePath == "/v1/chat/completions")
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("chat-model", body.RootElement.GetProperty("model").GetString());
                calls++;
                return JsonResponse("""
                    {"id":"chatcmpl-1","object":"chat.completion","created":1,"model":"chat-model",
                     "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},
                     "finish_reason":"stop"}]}
                    """);
            }

            Assert.Equal("/v1/embeddings", request.RequestUri.AbsolutePath);
            Assert.Equal("embedding-model", body.RootElement.GetProperty("model").GetString());
            calls++;
            return JsonResponse("""
                {"object":"list","model":"embedding-model",
                 "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
                 "usage":{"prompt_tokens":2,"total_tokens":2}}
                """);
        });

        var chatOptions = JsonSerializer.Deserialize<AIHappey.ChatCompletions.Models.ChatCompletionOptions>(
            """{"model":"chat-model","messages":[{"role":"user","content":"hello"}]}""",
            JsonSerializerOptions.Web)!;
        chatOptions.Headers = new() { ["Authorization"] = "caller-key", ["X-MindsHub-Key"] = "caller-key" };
        var chat = await provider.CompleteChatAsync(chatOptions);
        var embedding = await provider.OpenAIEmbeddingRequestAsync(JsonSerializer.Deserialize<AIHappey.Core.Models.OpenAIEmbeddingRequest>(
            """{"model":"embedding-model","input":"hello"}""",
            JsonSerializerOptions.Web)!);

        Assert.Equal("mindshub/chat-model", chat.Model);
        Assert.Equal("mindshub/embedding-model", embedding.Model);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Chat_completions_stream_through_the_native_sse_endpoint()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/chat/completions", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var count = 0;
        await foreach (var _ in provider.CompleteChatStreamingAsync(
            JsonSerializer.Deserialize<AIHappey.ChatCompletions.Models.ChatCompletionOptions>(
                """{"model":"chat-model","messages":[{"role":"user","content":"hello"}]}""",
                JsonSerializerOptions.Web)!))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Responses_stream_through_the_native_sse_endpoint()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var count = 0;
        await foreach (var _ in provider.ResponsesStreamingAsync(
            JsonSerializer.Deserialize<AIHappey.Responses.ResponseRequest>(
                """{"model":"responses-model","input":"hello"}""",
                JsonSerializerOptions.Web)!))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Embedding_contract_maps_values_and_provider_usage()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/embeddings", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("embedding-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("hello", body.RootElement.GetProperty("input")[0].GetString());
            return JsonResponse("""
                {"object":"list","model":"embedding-model",
                 "data":[{"object":"embedding","index":0,"embedding":[0.1,0.2]}],
                 "usage":{"prompt_tokens":3,"total_tokens":3}}
                """);
        });

        var response = await provider.EmbeddingRequestAsync(new()
        {
            Model = "embedding-model",
            Values = ["hello"]
        });

        Assert.Equal(2, response.Embeddings.Single().Count());
        Assert.Equal(3, response.Usage!.Tokens);
    }

    [Fact]
    public async Task Messages_request_uses_anthropic_route_version_and_mindshub_bearer_auth()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("messages-model", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""
                {"id":"msg_1","type":"message","role":"assistant","model":"messages-model",
                 "content":[{"type":"text","text":"hello"}],"stop_reason":"end_turn",
                 "stop_sequence":null,"usage":{"input_tokens":1,"output_tokens":1}}
                """);
        });

        var response = await provider.MessagesAsync(
            JsonSerializer.Deserialize<AIHappey.Messages.MessagesRequest>(
                """{"model":"messages-model","max_tokens":32,"messages":[{"role":"user","content":"hello"}]}""",
                JsonSerializerOptions.Web)!,
            new Dictionary<string, string>
            {
                ["Authorization"] = "caller-key",
                ["X-Api-Key"] = "caller-key",
                ["X-MindsHub-Key"] = "caller-key"
            });

        Assert.Equal("mindshub/messages-model", response.Model);
    }

    [Fact]
    public async Task Messages_stream_through_the_native_sse_endpoint()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/messages", request.RequestUri!.AbsolutePath);
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.True(body.RootElement.GetProperty("stream").GetBoolean());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("data: [DONE]\n\n", Encoding.UTF8, "text/event-stream")
            };
        });

        var count = 0;
        await foreach (var _ in provider.MessagesStreamingAsync(
            JsonSerializer.Deserialize<AIHappey.Messages.MessagesRequest>(
                """{"model":"messages-model","max_tokens":32,"messages":[{"role":"user","content":"hello"}]}""",
                JsonSerializerOptions.Web)!,
            new Dictionary<string, string>()))
            count++;

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Responses_request_uses_the_native_openai_responses_route()
    {
        var provider = CreateProvider(async (request, cancellationToken) =>
        {
            Assert.Equal("/v1/responses", request.RequestUri!.AbsolutePath);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert.Equal("responses-model", body.RootElement.GetProperty("model").GetString());
            return JsonResponse("""
                {"id":"resp_1","object":"response","created_at":1,"status":"completed",
                 "model":"responses-model","output":[]}
                """);
        });

        var options = JsonSerializer.Deserialize<AIHappey.Responses.ResponseRequest>(
            """{"model":"responses-model","input":"hello","store":false}""",
            JsonSerializerOptions.Web)!;
        options.Headers = new() { ["Authorization"] = "caller-key", ["X-MindsHub-Key"] = "caller-key" };
        await provider.ResponsesAsync(options);
    }

    [Fact]
    public void Registration_and_header_auth_are_wired()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-MindsHub-Key"] = " test-key ";
        var resolver = new HeaderApiKeyResolver(
            new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("test-key", resolver.Resolve("MINDSHUB"));
        Assert.Equal("X-MindsHub-Key", HeaderApiKeyResolver.SupportedProviderHeaders["mindshub"]);

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(MindsHubProvider));
    }

    [Fact]
    public async Task Decisions_reject_sdk_image_parts_before_sending()
    {
        var provider = CreateProvider((_, _) =>
            throw new InvalidOperationException("Image decisions must not reach MindsHub."));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(new()
        {
            Model = "mindshub/jev",
            StateFormat = "sdk",
            State = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    type = "file",
                    mediaType = "image/png",
                    data = new { type = "data", data = "AQ==" }
                }
            }),
            Questions = new()
            {
                ["visible"] = new DecisionBooleanQuestion { Instructions = "Is anything visible?" }
            }
        }));
    }

    private static MindsHubProvider CreateProvider(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(), new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage JsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class KeyResolver : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "mindshub" ? "test-key" : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => responder(request, cancellationToken);
    }
}
