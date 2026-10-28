using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.MachineFi;
using AIHappey.HeaderAuth;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.MachineFi;

public sealed class MachineFiProviderTests
{
    [Fact]
    public async Task Decision_uses_native_route_payload_and_maps_response()
    {
        Uri? requestUri = null;
        string? authorization = null;
        string? idempotencyKey = null;
        string? requestJson = null;
        var provider = CreateProvider("spark-key", request =>
        {
            requestUri = request.RequestUri;
            authorization = request.Headers.Authorization?.ToString();
            idempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""
                {
                  "model": "trio-spark-v1.3",
                  "choice_id": "open_export",
                  "probabilities": { "open_export": 0.9, "wait": 0.1 },
                  "status": "completed",
                  "usage": { "input_tokens": 25, "output_tokens": 0, "total_tokens": 25 }
                }
                """);
        });

        var response = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "machinefi/trio-spark-v1.3",
            State = JsonSerializer.SerializeToElement(new { screen = "Report details", image = "data:image/jpeg;base64,AQID" }),
            Headers = new() { ["Idempotency-Key"] = "request-123" },
            Questions = new()
            {
                ["next_action"] = new DecisionChoiceQuestion
                {
                    Instructions = "Choose the next action.",
                    Criteria = new()
                    {
                        ["open_export"] = "Open the Export menu",
                        ["wait"] = "Wait for more information"
                    }
                }
            }
        });

        Assert.Equal("/api/spark/v1/decisions", requestUri!.AbsolutePath);
        Assert.Equal("Bearer spark-key", authorization);
        Assert.Equal("request-123", idempotencyKey);
        using var requestBody = JsonDocument.Parse(requestJson!);
        var payload = requestBody.RootElement;
        Assert.Equal("trio-spark-v1.3", payload.GetProperty("model").GetString());
        Assert.Equal("Choose the next action.", payload.GetProperty("task").GetString());
        Assert.Equal("Report details", payload.GetProperty("state").GetProperty("screen").GetString());
        Assert.Equal("data:image/jpeg;base64,AQID", payload.GetProperty("state").GetProperty("image").GetString());
        Assert.Equal("Open the Export menu", payload.GetProperty("choices")[0].GetProperty("description").GetString());

        var answer = Assert.IsType<DecisionChoiceAnswer>(response.Answers["next_action"]);
        Assert.Equal("open_export", answer.Choice);
        Assert.Equal(0.9, answer.Probabilities!["open_export"]);
        Assert.Equal(25, response.Usage!.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
    }

    [Fact]
    public async Task Decision_generates_idempotency_key_and_preserves_sampled_frames_in_state()
    {
        string? idempotencyKey = null;
        string? requestJson = null;
        var provider = CreateProvider("spark-key", request =>
        {
            idempotencyKey = request.Headers.GetValues("Idempotency-Key").Single();
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""
                {
                  "choice_id": "continue",
                  "probabilities": [
                    { "id": "continue", "probability": 0.7 },
                    { "id": "stop", "probability": 0.3 }
                  ],
                  "usage": { "input_tokens": 10, "output_tokens": 0 }
                }
                """);
        });

        await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "trio-spark-v1.3",
            State = JsonSerializer.SerializeToElement(new
            {
                frames = new[]
                {
                    new { timestamp = "00:00:01.000", image = "data:image/jpeg;base64,AQID" },
                    new { timestamp = "00:00:02.000", image = "data:image/jpeg;base64,BAUG" }
                }
            }),
            Questions = new()
            {
                ["action"] = new DecisionChoiceQuestion
                {
                    Instructions = "Select an action.",
                    Criteria = new() { ["continue"] = "Continue", ["stop"] = "Stop" }
                }
            }
        });

        Assert.False(string.IsNullOrWhiteSpace(idempotencyKey));
        using var requestBody = JsonDocument.Parse(requestJson!);
        Assert.Equal(2, requestBody.RootElement.GetProperty("state").GetProperty("frames").GetArrayLength());
    }

    [Fact]
    public async Task OpenAI_decision_contract_maps_to_native_choice_request()
    {
        string? requestJson = null;
        var provider = CreateProvider("spark-key", request =>
        {
            requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json("""
                {
                  "choice": "approve",
                  "probabilities": { "approve": 0.8, "decline": 0.2 },
                  "usage": { "input_tokens": 8, "output_tokens": 0 }
                }
                """);
        });

        var response = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "trio-spark-v1.3",
            Input = "A purchase request for $10.",
            Questions =
            [
                new OpenAIDecisionChoiceQuestion
                {
                    Name = "action",
                    Instructions = "Choose whether to approve.",
                    Choices =
                    [
                        new() { Value = "approve", Description = "Approve the purchase" },
                        new() { Value = "decline", Description = "Decline the purchase" }
                    ]
                }
            ]
        });

        using var requestBody = JsonDocument.Parse(requestJson!);
        Assert.Equal("A purchase request for $10.", requestBody.RootElement.GetProperty("state").GetString());
        var answer = Assert.IsType<OpenAIDecisionChoiceAnswer>(Assert.Single(response.Answers));
        Assert.Equal("approve", answer.Choice.Value.GetString());
        Assert.Equal(0.8, answer.Confidence);
        Assert.Equal(8, response.Usage.InputTokens);
    }

    [Fact]
    public async Task Decisions_reject_unsupported_models_and_question_types_before_sending()
    {
        var provider = CreateProvider("spark-key", _ => throw new InvalidOperationException("Request should not be sent."));
        var request = new DecisionRequest
        {
            Model = "trio-spark-v1.2",
            State = "state",
            Questions = new()
            {
                ["choice"] = new DecisionChoiceQuestion
                {
                    Instructions = "Choose.",
                    Criteria = new() { ["a"] = "A", ["b"] = "B" }
                }
            }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));

        request.Model = "trio-spark-v1.3";
        request.Questions["choice"] = new DecisionBooleanQuestion { Instructions = "Yes?" };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));

        request.Questions["choice"] = new DecisionChoiceQuestion
        {
            Instructions = "Choose.",
            Criteria = new() { ["only"] = "Only one" }
        };
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.DecisionRequestAsync(request));
    }

    [Fact]
    public async Task Model_catalog_and_provider_registration_expose_decision_model()
    {
        var provider = CreateProvider(null, _ => throw new InvalidOperationException("Model listing should not send a request."));
        var model = Assert.Single(await provider.ListModels());
        Assert.Equal("machinefi/trio-spark-v1.3", model.Id);
        Assert.Equal("decision", model.Type);

        var context = new DefaultHttpContext();
        context.Request.Headers["X-MachineFi-Key"] = " spark-key ";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("spark-key", resolver.Resolve("machinefi"));

        var services = new ServiceCollection();
        services.AddProviders();
        Assert.Contains(services, descriptor => descriptor.ImplementationType == typeof(MachineFiProvider));
    }

    private static MachineFiProvider CreateProvider(
        string? key,
        Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(responder))));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "machinefi" ? key : null;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
