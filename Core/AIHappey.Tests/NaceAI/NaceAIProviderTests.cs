using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.NaceAI;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.NaceAI;

public sealed class NaceAIProviderTests
{
    [Fact]
    public async Task ListModels_uses_live_models_skips_retired_and_types_decisions()
    {
        HttpRequestMessage? captured = null;
        var provider = CreateProvider("nace_sk_test", (request, _) =>
        {
            captured = request;
            return Task.FromResult(Json("""
                {"models":[
                  {"name":"drex-v2.0","description":"Drex 2.0","release_date":"2026-10-06","alias_for":null},
                  {"name":"drex-latest","description":"Points to drex-v2.0","release_date":"2026-10-06","alias_for":"drex-v2.0"},
                  {"name":"drex-v1.1","description":"Retired on 2026-09-28.","release_date":"2026-09-25","alias_for":"drex-v1.5"}
                ],"request_id":"req_1"}
                """));
        });

        var models = (await provider.ListModels()).ToList();

        Assert.Equal("/v1/models", captured!.RequestUri!.AbsolutePath);
        Assert.Equal("nace_sk_test", captured.Headers.Authorization!.Parameter);
        Assert.Equal("Bearer", captured.Headers.Authorization.Scheme);
        Assert.DoesNotContain(models, m => m.Id == "naceai/drex-v1.1");
        Assert.Equal("decision", models.Single(m => m.Id == "naceai/drex-v2.0").Type);
        Assert.Contains(models, m => m.Id == "naceai/drex-latest");
        Assert.Contains(models, m => m.Id == "naceai/parse" && m.Type == "language");
        Assert.Contains(models, m => m.Id == "naceai/browser-run");
    }

    [Fact]
    public async Task Decisions_map_boolean_choice_and_score_questions()
    {
        string? body = null;
        HttpRequestMessage? captured = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            captured = request;
            body = await request.Content!.ReadAsStringAsync();
            return Json("""
                {"model":"drex-v2.0","answers":{
                  "ok":{"noul":0.9},
                  "kind":{"choice":"bug","confidence":0.8,"probabilities":{"bug":0.8,"feature":0.2}},
                  "tone":{"score":1.4,"legend":{"0":"bad","1":"ok","2":"great"},"confidence":0.6,"probabilities":{"0":0.1,"1":0.4,"2":0.5}}},
                 "usage":{"input_tokens":12,"output_tokens":0},"evaluation_time_ms":5,"request_id":"req_9"}
                """);
        });

        var response = await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "naceai/drex-v2.0",
            State = "Ticket text",
            Questions = new()
            {
                ["ok"] = new DecisionBooleanQuestion { Instructions = "Is it fine?" },
                ["kind"] = new DecisionChoiceQuestion
                {
                    Instructions = "Kind?",
                    Criteria = new() { ["bug"] = null, ["feature"] = null }
                },
                ["tone"] = new DecisionScoreQuestion
                {
                    Instructions = "Tone?",
                    Criteria = [JsonSerializer.SerializeToElement("bad"), JsonSerializer.SerializeToElement("ok"), JsonSerializer.SerializeToElement("great")]
                }
            }
        });

        Assert.Equal("/v1/systemone", captured!.RequestUri!.AbsolutePath);
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("drex-v2.0", doc.RootElement.GetProperty("model").GetString());
        var questions = doc.RootElement.GetProperty("questions");
        Assert.Equal("noul", questions.GetProperty("ok").GetProperty("type").GetString());
        Assert.Equal("choice", questions.GetProperty("kind").GetProperty("type").GetString());
        Assert.Equal("score", questions.GetProperty("tone").GetProperty("type").GetString());
        Assert.False(doc.RootElement.TryGetProperty("images", out _));

        Assert.Equal(0.9, ((DecisionBooleanAnswer)response.Answers["ok"]).Probability);
        Assert.Equal("bug", ((DecisionChoiceAnswer)response.Answers["kind"]).Choice);
        Assert.Equal(1.4, ((DecisionScoreAnswer)response.Answers["tone"]).Score);
        Assert.Equal(12, response.Usage!.InputTokens);
    }

    [Fact]
    public async Task Decisions_reject_images_for_text_models_and_unsupported_inputs()
    {
        var provider = CreateProvider("k", (_, _) => throw new InvalidOperationException("must not call"));
        var image = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "input_text", text = "look" },
            new { type = "input_image", image_url = "data:image/png;base64,AAAA" }
        });
        var questions = new Dictionary<string, DecisionQuestion> { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "drex-v2.0", State = new DecisionInput(image), Questions = questions
        }));
        await Assert.ThrowsAnyAsync<NotSupportedException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "drex-v2.0", State = "x", MaxOutputTokens = 5, Questions = questions
        }));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "drex-v2.0", State = "x", Questions = Enumerable.Range(0, 513).ToDictionary(i => $"q{i}", _ => (DecisionQuestion)new DecisionBooleanQuestion { Instructions = "q" })
        }));
    }

    [Fact]
    public async Task Decisions_send_images_for_drex_max_and_keep_request_id_in_errors()
    {
        string? body = null;
        var provider = CreateProvider("k", async (request, _) =>
        {
            body = await request.Content!.ReadAsStringAsync();
            return Json("""{"model":"drex-max","answers":{"a":{"noul":0.2}},"usage":{"input_tokens":1,"output_tokens":0}}""");
        });
        var state = JsonSerializer.SerializeToElement(new object[]
        {
            new { type = "input_text", text = "look" },
            new { type = "input_image", image_url = "data:image/png;base64,AAAA" }
        });

        await provider.DecisionRequestAsync(new DecisionRequest
        {
            Model = "drex-max", State = new DecisionInput(state),
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        });
        using var doc = JsonDocument.Parse(body!);
        Assert.Equal("look", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("data:image/png;base64,AAAA", doc.RootElement.GetProperty("images")[0].GetString());

        var failing = CreateProvider("k", (_, _) =>
        {
            var response = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":\"slow\"}") };
            response.Headers.TryAddWithoutValidation("x-request-id", "req_42");
            response.Headers.TryAddWithoutValidation("Retry-After", "7");
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => failing.DecisionRequestAsync(new DecisionRequest
        {
            Model = "drex-v2.0", State = "x",
            Questions = new() { ["a"] = new DecisionBooleanQuestion { Instructions = "q" } }
        }));
        Assert.Contains("req_42", error.Message);
        Assert.Contains("Retry-After: 7", error.Message);
        Assert.Equal(HttpStatusCode.TooManyRequests, error.StatusCode);
    }

    [Fact]
    public async Task OpenAI_decisions_map_through_the_native_systemone_flow()
    {
        var provider = CreateProvider("k", (_, _) => Task.FromResult(Json("""
            {"model":"drex-v2.0","answers":{
              "question-0":{"noul":0.75},
              "question-1":{"choice":"option-1","confidence":0.7,"probabilities":{"option-0":0.3,"option-1":0.7}}},
             "usage":{"input_tokens":5,"output_tokens":0}}
            """)));

        var response = await provider.OpenAIDecisionRequestAsync(new OpenAIDecisionRequest
        {
            Model = "drex-v2.0",
            Input = "hello",
            Questions =
            [
                new OpenAIDecisionPredicateQuestion { Instructions = "Is it?" },
                new OpenAIDecisionChoiceQuestion
                {
                    Instructions = "Which?",
                    Choices = [new() { Value = "a" }, new() { Value = "b" }]
                }
            ]
        });

        Assert.Equal(0.75, ((OpenAIDecisionPredicateAnswer)response.Answers[0]).Probability);
        Assert.Equal("b", ((OpenAIDecisionChoiceAnswer)response.Answers[1]).Choice.Value.GetString());
        Assert.Equal(5, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Parse_url_requests_markdown_and_returns_it_as_text()
    {
        var requests = new List<(HttpRequestMessage Request, string Body)>();
        var provider = CreateProvider("k", async (request, _) =>
        {
            requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
            return Json("""
                {"job_id":"job-1","kind":"parse","status":"succeeded","credits":2,"cost":{"total_usd":"0.02"},
                 "result":{"document":{"markdown":"# Invoice","page_count":1},"disclosures":[{"code":"content_truncated"}]}}
                """);
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest("naceai/parse-high",
            new AIFileContentPart { Type = "file", Data = "https://example.com/a.pdf", Filename = "a.pdf", MediaType = "application/pdf" }));

        var call = Assert.Single(requests);
        Assert.Equal("/v1/documents/parse", call.Request.RequestUri!.AbsolutePath);
        Assert.Equal("wait_seconds=60", call.Request.RequestUri.Query.TrimStart('?'));
        Assert.True(call.Request.Headers.Contains("Idempotency-Key"));
        using var doc = JsonDocument.Parse(call.Body);
        Assert.Equal("high", doc.RootElement.GetProperty("parse_mode").GetString());
        Assert.Equal("url", doc.RootElement.GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("markdown", doc.RootElement.GetProperty("output").GetProperty("formats")[0].GetString());

        var text = response.Output!.Items!.Last().Content!.OfType<AITextContentPart>().Single().Text;
        Assert.Equal("# Invoice", text);
        Assert.Equal("naceai/parse-high", response.Model);
        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Parse_polls_accepted_jobs_until_they_succeed()
    {
        var calls = new List<string>();
        var provider = CreateProvider("k", (request, _) =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            return Task.FromResult(request.Method == HttpMethod.Post
                ? Json("""{"job_id":"job-2","kind":"parse","status":"queued","result":null}""")
                : Json("""{"job_id":"job-2","kind":"parse","status":"succeeded","result":{"document":{"markdown":"done"}}}"""));
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest("naceai/parse",
            new AIFileContentPart { Type = "file", Data = "https://example.com/a.pdf" }));

        Assert.Equal(["POST /v1/documents/parse", "GET /v1/documents/jobs/job-2"], calls);
        Assert.Equal("done", response.Output!.Items!.Last().Content!.OfType<AITextContentPart>().Single().Text);
    }

    [Fact]
    public async Task Parse_cancellation_deletes_the_job()
    {
        var calls = new List<string>();
        using var cts = new CancellationTokenSource();
        var provider = CreateProvider("k", (request, _) =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get)
                cts.Cancel();
            return Task.FromResult(request.Method == HttpMethod.Delete
                ? Json("{}")
                : Json("""{"job_id":"job-3","kind":"parse","status":"running","result":null}"""));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(
            CreateRequest("naceai/parse", new AIFileContentPart { Type = "file", Data = "https://example.com/a.pdf" }), cts.Token));

        Assert.Contains("DELETE /v1/documents/jobs/job-3", calls);
    }

    [Fact]
    public async Task Base64_documents_are_uploaded_without_the_api_key_and_referenced_as_workspace_files()
    {
        var calls = new List<(string Path, string? Auth, string? UploadToken, string Body)>();
        var provider = CreateProvider("secret-key", async (request, _) =>
        {
            calls.Add((request.RequestUri!.AbsolutePath, request.Headers.Authorization?.Parameter,
                request.Headers.TryGetValues("X-Upload-Token", out var token) ? token.Single() : null,
                request.Content is MultipartFormDataContent ? "multipart" : request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/documents/upload-grants" => Json("""{"workspace_id":"ws-1","upload_url":"/v1/uploads/ws-1","token":"upl-token"}"""),
                "/v1/uploads/ws-1" => Json("""{"result":{"file":{"workspace_id":"ws-1","file_id":"file-1"}}}"""),
                _ => Json("""{"job_id":"j","kind":"parse","status":"succeeded","result":{"document":{"markdown":"ok"}}}""")
            };
        });

        await provider.ExecuteUnifiedAsync(CreateRequest("naceai/parse",
            new AIFileContentPart { Type = "file", Data = "data:application/pdf;base64," + Convert.ToBase64String("%PDF-1.7"u8.ToArray()), Filename = "x.pdf", MediaType = "application/pdf" }));

        Assert.Equal("secret-key", calls[0].Auth);
        Assert.Equal("/v1/uploads/ws-1", calls[1].Path);
        Assert.Null(calls[1].Auth);
        Assert.Equal("upl-token", calls[1].UploadToken);
        using var doc = JsonDocument.Parse(calls[2].Body);
        var source = doc.RootElement.GetProperty("source");
        Assert.Equal("workspace_file", source.GetProperty("type").GetString());
        Assert.Equal("file-1", source.GetProperty("file_id").GetString());
    }

    [Fact]
    public async Task Extract_classify_split_and_ground_send_their_documented_bodies()
    {
        var bodies = new Dictionary<string, JsonElement>();
        var provider = CreateProvider("k", async (request, _) =>
        {
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            bodies[request.RequestUri!.AbsolutePath] = doc.RootElement.Clone();
            return Json("""{"job_id":"j","status":"succeeded","result":{"data":{"total":5},"units":[{"class":"invoice"}],"segments":[],"targets":[]}}""");
        });
        var file = new AIFileContentPart { Type = "file", Data = "https://example.com/a.pdf" };

        var extract = await provider.ExecuteUnifiedAsync(CreateRequest("naceai/extract", [file],
            new() { ["naceai"] = JsonDocument.Parse("""{"schema":{"type":"object"}}""").RootElement.Clone() }));
        await provider.ExecuteUnifiedAsync(CreateRequest("naceai/classify", [file],
            new() { ["naceai"] = JsonDocument.Parse("""{"classes":[{"id":"invoice","label":"Invoice"}]}""").RootElement.Clone() }));
        await provider.ExecuteUnifiedAsync(CreateRequest("naceai/split", [file],
            new() { ["naceai"] = JsonDocument.Parse("""{"classes":[{"id":"invoice","label":"Invoice"}],"unknown_policy":"include"}""").RootElement.Clone() }));
        await provider.ExecuteUnifiedAsync(CreateRequest("naceai/ground", [file, new AITextContentPart { Type = "text", Text = "Total is 5" }]));

        Assert.Equal("object", bodies["/v1/documents/extract"].GetProperty("schema").GetProperty("type").GetString());
        Assert.Equal("invoice", bodies["/v1/documents/classify"].GetProperty("classes")[0].GetProperty("id").GetString());
        Assert.Equal("include", bodies["/v1/documents/split"].GetProperty("unknown_policy").GetString());
        Assert.Equal("Total is 5", bodies["/v1/documents/ground"].GetProperty("targets")[0].GetProperty("text").GetString());
        Assert.Contains("\"total\"", extract.Output!.Items!.Last().Content!.OfType<AITextContentPart>().Single().Text);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest("naceai/classify", [file])));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(CreateRequest("naceai/parse", [file],
            new() { ["naceai"] = JsonDocument.Parse("""{"unknown":1}""").RootElement.Clone() })));
    }

    [Fact]
    public async Task Failed_documents_surface_the_error_with_request_id()
    {
        var provider = CreateProvider("k", (_, _) =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PaymentRequired) { Content = new StringContent("{\"error\":\"credits\"}") };
            response.Headers.TryAddWithoutValidation("x-request-id", "req_7");
            return Task.FromResult(response);
        });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(
            CreateRequest("naceai/parse", new AIFileContentPart { Type = "file", Data = "https://example.com/a.pdf" })));
        Assert.Contains("req_7", error.Message);
        Assert.Equal(HttpStatusCode.PaymentRequired, error.StatusCode);
    }

    [Fact]
    public async Task Browser_run_starts_polls_and_returns_the_answer()
    {
        var calls = new List<(string Method, string Path, string Body)>();
        var provider = CreateProvider("k", async (request, _) =>
        {
            calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
            return request.Method == HttpMethod.Post
                ? Json("""{"id":"run-1","object":"browser_run","status":"queued","task":"t","start_url":"https://example.com/"}""")
                : Json("""{"id":"run-1","status":"succeeded","answer_text":"42","outcome":{"kind":"completed"},"usage":{"drex_input_tokens":3,"llm_input_tokens":4,"llm_output_tokens":5,"cost_usd":"0.05"}}""");
        });

        var response = await provider.ExecuteUnifiedAsync(CreateRequest("naceai/browser-run",
            [new AITextContentPart { Type = "text", Text = "Find the answer on https://example.com/page" }]));

        Assert.Equal("POST", calls[0].Method);
        Assert.Equal("/v1/browser/runs", calls[0].Path);
        using var doc = JsonDocument.Parse(calls[0].Body);
        Assert.Equal("https://example.com/page", doc.RootElement.GetProperty("start_url").GetString());
        Assert.Equal("https://example.com", doc.RootElement.GetProperty("allowed_origins")[0].GetString());
        Assert.Equal("/v1/browser/runs/run-1", calls[1].Path);
        Assert.Equal("42", response.Output!.Items!.Last().Content!.OfType<AITextContentPart>().Single().Text);
        Assert.Equal("completed", response.Status);
    }

    [Fact]
    public async Task Browser_run_cancellation_cancels_the_run()
    {
        var calls = new List<string>();
        using var cts = new CancellationTokenSource();
        var provider = CreateProvider("k", (request, _) =>
        {
            calls.Add($"{request.Method} {request.RequestUri!.AbsolutePath}");
            if (request.Method == HttpMethod.Get)
                cts.Cancel();
            return Task.FromResult(Json("""{"id":"run-2","status":"running"}"""));
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(
            CreateRequest("naceai/browser-run", [new AITextContentPart { Type = "text", Text = "go" }],
                new() { ["naceai"] = JsonDocument.Parse("""{"start_url":"https://example.com"}""").RootElement.Clone() }), cts.Token));

        Assert.Contains("POST /v1/browser/runs/run-2/cancel", calls);
    }

    [Fact]
    public async Task Missing_key_and_non_special_chat_models_fail_clearly()
    {
        var provider = CreateProvider(null, (_, _) => throw new InvalidOperationException("no http"));
        Assert.Empty(await provider.ListModels());
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ResponsesAsync(new() { Model = "naceai/drex-v2.0", Input = "hi" }));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.ImageRequest(new() { Model = "x", Prompt = "p" }, default));
    }

    private static AIRequest CreateRequest(string model, AIContentPart part, Dictionary<string, object?>? metadata = null)
        => CreateRequest(model, [part], metadata);

    private static AIRequest CreateRequest(string model, List<AIContentPart> parts, Dictionary<string, object?>? metadata = null)
        => new()
        {
            ProviderId = "naceai",
            Model = model,
            Input = new AIInput { Items = [new AIInputItem { Role = "user", Content = parts }] },
            Metadata = metadata
        };

    private static NaceAIProvider CreateProvider(
        string? key,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        => new(new KeyResolver(key),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new ClientFactory(responder));

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => provider == "naceai" ? key : null;
    }

    // Each call returns a new client, like the real factory, so the upload client carries no default bearer header.
    private sealed class ClientFactory(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(responder));
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => responder(request, ct);
    }
}
