using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.SubQ;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.SubQ;

public sealed class SubQProviderTests
{
    private const string Png = "data:image/png;base64,iVBORw0KGgo=";

    [Fact]
    public async Task Chat_and_responses_use_documented_routes_and_bearer_auth()
    {
        var requests = new List<(Uri Uri, string Body, string? Key)>();
        var provider = CreateProvider("k", async (request, _) =>
        {
            requests.Add((request.RequestUri!, await request.Content!.ReadAsStringAsync(), request.Headers.Authorization?.Parameter));
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("err") };
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "subq/subq-preview", Messages = [] }));
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.ResponsesAsync(new() { Model = "subq/subq-preview", Input = "hi" }));

        Assert.Equal(["/v1/chat/completions", "/v1/responses"], requests.Select(r => r.Uri.AbsolutePath));
        Assert.All(requests, r => Assert.Equal("k", r.Key));
        Assert.All(requests, r => Assert.Equal("subq-preview", JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString()));
    }

    [Fact]
    public async Task Chat_sends_max_tokens_instead_of_max_completion_tokens()
    {
        string? body = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("err") };
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new()
        {
            Model = "subq-preview",
            Messages = [],
            AdditionalProperties = new() { ["max_completion_tokens"] = JsonSerializer.SerializeToElement(50) }
        }));

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal(50, doc.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.False(doc.RootElement.TryGetProperty("max_completion_tokens", out _));
    }

    [Fact]
    public async Task Chat_forbidden_explains_early_access()
    {
        var provider = CreateProvider("k", (_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"detail\":{\"message\":\"no\"}}") }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.CompleteChatAsync(new() { Model = "subq-preview", Messages = [] }));
        Assert.Contains("early access", error.Message);
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Chat_and_responses_reject_decision_models_without_http()
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("must not call"));

        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CompleteChatAsync(new() { Model = "subq/subq-snap-1.1", Messages = [] }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new() { Model = "subq-snap-1.1", Input = "x" }));
    }

    [Fact]
    public async Task Decision_maps_all_question_types_and_images()
    {
        HttpRequestMessage? captured = null;
        string? body = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json("""
                {"model":"subq-snap-1.1","answers":{
                  "escalate":{"type":"noul","noul":0.92},
                  "queue":{"type":"choice","choice":"nurse","probabilities":{"nurse":0.9,"desk":0.1},"confidence":0.9},
                  "acuity":{"type":"score","score":1.5,"legend":{"0":"low","1":"mid","2":"high"},"probabilities":{"0":0,"1":0.5,"2":0.5},"confidence":0.5}},
                 "usage":{"input_tokens":477,"output_tokens":0}}
                """);
        });

        var response = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq/subq-snap-1.1",
            State = new DecisionInput(JsonDocument.Parse($$"""[{"type":"input_text","text":"note"},{"type":"input_image","image_url":"{{Png}}"}]""").RootElement.Clone()),
            Questions = new()
            {
                ["escalate"] = new DecisionBooleanQuestion { Instructions = "Escalate?" },
                ["queue"] = new DecisionChoiceQuestion
                {
                    Instructions = "Team?",
                    Criteria = new() { ["nurse"] = new DecisionInput("n"), ["desk"] = null }
                },
                ["acuity"] = new DecisionScoreQuestion
                {
                    Instructions = "When?",
                    Criteria = [new DecisionInput("low"), new DecisionInput("mid"), new DecisionInput("high")]
                }
            }
        });

        Assert.Equal("/v1/systemone", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;
        Assert.Equal("subq-snap-1.1", root.GetProperty("model").GetString());
        Assert.Equal("note", root.GetProperty("state").GetString());
        var image = Assert.Single(root.GetProperty("images").EnumerateArray());
        Assert.Equal("image/png", image.GetProperty("mediaType").GetString());
        Assert.Equal("iVBORw0KGgo=", image.GetProperty("base64").GetString());
        Assert.Equal("noul", root.GetProperty("questions").GetProperty("escalate").GetProperty("type").GetString());

        Assert.Equal(0.92, Assert.IsType<DecisionBooleanAnswer>(response.Answers["escalate"]).Probability, 3);
        Assert.Equal("nurse", Assert.IsType<DecisionChoiceAnswer>(response.Answers["queue"]).Choice);
        Assert.Equal(1.5, Assert.IsType<DecisionScoreAnswer>(response.Answers["acuity"]).Score, 3);
        Assert.Equal(477, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
    }

    [Theory]
    [InlineData("data:image/gif;base64,R0lGODlh")]
    [InlineData("https://example.com/a.png")]
    public async Task Decision_rejects_unsupported_image_inputs_without_http(string image)
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("must not call"));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq-snap-1.1",
            State = new DecisionInput(JsonDocument.Parse($$"""[{"type":"input_text","text":"x"},{"type":"input_image","image_url":"{{image}}"}]""").RootElement.Clone()),
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
    }

    [Fact]
    public async Task Decision_rejects_more_than_four_images_and_unsupported_options()
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("must not call"));
        var parts = string.Join(",", Enumerable.Repeat($$"""{"type":"input_image","image_url":"{{Png}}"}""", 5));

        await Assert.ThrowsAsync<ArgumentException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq-snap-1.1",
            State = new DecisionInput(JsonDocument.Parse($$"""[{"type":"input_text","text":"x"},{{parts}}]""").RootElement.Clone()),
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq-snap-1.1", State = "x", MaxOutputTokens = 5,
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq-preview", State = "x",
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
    }

    [Theory]
    [InlineData(402)]
    [InlineData(403)]
    [InlineData(422)]
    [InlineData(429)]
    public async Task Decision_surfaces_documented_errors(int status)
    {
        var provider = CreateProvider("k", (_, _) =>
            Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent("""{"detail":{"error_type":"validation_error","message":"images/0: image over 4 MiB"}}""")
            }));

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "subq-snap-1.1", State = "x",
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Contains("validation_error: images/0: image over 4 MiB", error.Message);
    }

    [Fact]
    public async Task OpenAI_decision_maps_through_systemone()
    {
        string? body = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json("""{"model":"subq-snap-1.1","answers":{"question-0":{"type":"noul","noul":0.8}},"usage":{"input_tokens":10,"output_tokens":0}}""");
        });

        var request = JsonSerializer.Deserialize<OpenAIDecisionRequest>("""
            {"model":"subq-snap-1.1","input":"The payment failed.","questions":[{"type":"predicate","name":"retry","instructions":"Retry?"}]}
            """, JsonSerializerOptions.Web)!;
        var response = await provider.OpenAIDecisionRequestAsync(request);

        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("The payment failed.", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal(0.8, Assert.IsType<OpenAIDecisionPredicateAnswer>(Assert.Single(response.Answers)).Probability, 3);
        Assert.Equal(10, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task ListModels_without_key_is_empty()
    {
        var provider = CreateProvider(null, (_, _) => throw new InvalidOperationException("no http"));
        Assert.Empty(await provider.ListModels());
    }

    [Fact]
    public async Task ListModels_applies_catalog_decision_type()
    {
        var provider = CreateProvider("k", (request, _) =>
        {
            Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Json("""{"object":"list","data":[{"id":"subq-preview","name":"SubQ Preview","object":"model","created":0,"owned_by":"subquadratic"},{"id":"subq-snap-1.1","name":"SubQ Snap 1.1","object":"model","created":0,"owned_by":"subquadratic"}]}"""));
        });

        var models = (await provider.ListModels()).ToList();
        Assert.Equal("language", models.Single(m => m.Id == "subq/subq-preview").Type);
        var snap = models.Single(m => m.Id == "subq/subq-snap-1.1");
        Assert.Equal("decision", snap.Type);
        Assert.Equal(393216, snap.ContextWindow);
    }

    [Fact]
    public void Provider_is_registered_with_header_mapping()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-SubQ-Key"] = " key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("key", resolver.Resolve("SUBQ"));

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, d => d.ImplementationType == typeof(SubQProvider));
        using var root = services.BuildServiceProvider();
        Assert.True(root.GetRequiredService<ProviderRegistry>().HasModelProvider("subq"));
    }

    private static SubQProvider CreateProvider(string? key, Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "subq" ? key : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }
}
