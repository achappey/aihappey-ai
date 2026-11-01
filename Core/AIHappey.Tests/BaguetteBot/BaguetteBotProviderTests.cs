using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.BaguetteBot;
using AIHappey.HeaderAuth;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.BaguetteBot;

public sealed class BaguetteBotProviderTests
{
    [Fact]
    public void Header_auth_advertises_baguettebot_key()
        => Assert.Equal("X-BaguetteBot-Key", HeaderApiKeyResolver.SupportedProviderHeaders["baguettebot"]);

    [Theory]
    [InlineData("data")]
    [InlineData("models")]
    public async Task Discovery_preserves_native_identity_context_vision_and_euro_prices(string collection)
    {
        var provider = Provider(request =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
            Assert.Equal("https://api.baguettebot.com", request.RequestUri!.GetLeftPart(UriPartial.Authority));
            return request.RequestUri.AbsolutePath switch
            {
                "/v1/models" => JsonResponse(new Dictionary<string, object?>
                {
                    [collection] = new[] { new { id = "anthropic/claude-x", name = "Claude X", context_length = 200000, vision = true,
                        eur_per_million_input_tokens = 1.25m, eur_per_million_output_tokens = 5.50m } },
                    ["computer_eur_per_hour"] = 2
                }),
                "/v1/agents" => JsonResponse(new { data = new[] { Agent() } }),
                _ => throw new InvalidOperationException()
            };
        });
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(2, models.Length);
        Assert.Equal("baguettebot/model/anthropic/claude-x", models[0].Id);
        Assert.Equal("baguettebot/agent/agent-1", models[1].Id);
        Assert.All(models, model =>
        {
            Assert.Equal(200000, model.ContextWindow);
            Assert.Contains("vision", model.Tags!);
            Assert.Null(model.Pricing);
            Assert.Contains("input=1.25", model.Description);
            Assert.Contains("output=5.50", model.Description);
        });
        Assert.Contains("underlying model: anthropic/claude-x", models[1].Description);
        Assert.False(typeof(IUnifiedModelProvider).IsAssignableFrom(typeof(BaguetteBotProvider)));
    }

    [Fact]
    public async Task Discovery_without_key_is_empty_and_malformed_catalog_is_explicit()
    {
        Assert.Empty(await Provider(_ => throw new InvalidOperationException(), key: null).ListModels());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(_ => JsonResponse(new { wrong = Array.Empty<object>() })).ListModels());
    }

    [Fact]
    public async Task Synchronous_execution_preserves_markdown_tools_ids_and_raw_options()
    {
        JsonElement payload = default;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(Agent());
            payload = Body(request);
            return JsonResponse(Run("done", "| Shop | Price |\n|---|---|\n| A | 1 |", messages:
            [
                new { id = "tool-1", role = "tool", content = "browser result", tool = new { name = "browser", ok = true, summary = "Found price" } },
                new { id = "assistant-1", role = "assistant", content = "| Shop | Price |\n|---|---|\n| A | 1 |" }
            ]));
        });
        using var options = JsonDocument.Parse("""{"content":"must not replace task","wait":true,"native_future":{"null_value":null,"values":[1,true]},"metadata":{"customer":"c42"}}""");
        var response = await provider.ExecuteUnifiedAsync(Request(options.RootElement));
        Assert.Equal("latest", payload.GetProperty("content").GetString());
        Assert.True(payload.GetProperty("wait").GetBoolean());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("native_future").GetProperty("null_value").ValueKind);
        Assert.Equal("c42", payload.GetProperty("metadata").GetProperty("customer").GetString());
        Assert.False(payload.TryGetProperty("agent_id", out _));
        Assert.Contains("|---|---|", TextOutput(response));
        var tool = Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AIToolCallContentPart>());
        Assert.Equal("tool-1", tool.ToolCallId);
        Assert.Equal("browser result", Assert.IsType<TextContentBlock>(Assert.Single(Assert.IsType<CallToolResult>(tool.Output).Content!)).Text);
        Assert.Equal("agent-1", response.Metadata!["baguettebot.agent_id"]);
        Assert.Equal("user-1", response.Metadata["baguettebot.message_id"]);
        Assert.Equal("assistant-1", response.Metadata["baguettebot.output_message_id"]);
        Assert.Equal("anthropic/claude-x", response.Metadata["baguettebot.model_id"]);
        Assert.Equal(2, Assert.IsType<JsonElement>(response.Metadata["baguettebot.raw"]).GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task Accepted_dispatch_is_not_fabricated_as_completed()
    {
        var calls = new List<string>();
        var provider = Provider(request =>
        {
            calls.Add(request.Method + " " + request.RequestUri!.AbsolutePath);
            return request.Method == HttpMethod.Get ? JsonResponse(Agent())
                : JsonResponse(Run("working"), HttpStatusCode.Accepted);
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { wait = false }));
        Assert.Equal("in_progress", response.Status);
        Assert.Equal("working", response.Metadata!["baguettebot.status"]);
        Assert.Empty(Assert.Single(response.Output!.Items!).Content!);
        Assert.Equal(["GET /v1/agents/agent-1", "POST /v1/agents/agent-1/messages"], calls);
        Assert.Equal("user-1", response.Metadata!["baguettebot.message_id"]);
    }

    [Theory]
    [InlineData(202)]
    [InlineData(200)]
    public async Task Wait_true_follows_long_running_tasks_and_reads_only_after_current_message(int status)
    {
        var gets = 0;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Post)
                return JsonResponse(Run("working"), (HttpStatusCode)status);
            if (request.RequestUri!.AbsolutePath.EndsWith("/messages"))
            {
                Assert.Contains("after=user-1", request.RequestUri.Query);
                return JsonResponse(new { data = new[] { Message("a1", "assistant", "**done**") } });
            }
            return JsonResponse(Agent(++gets == 2 ? "working" : "idle"));
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { poll_interval_ms = 1 }));
        Assert.Equal("completed", response.Status);
        Assert.Equal("**done**", TextOutput(response));
        Assert.Equal(3, gets);
    }

    [Fact]
    public async Task Continuity_reuses_agent_without_replaying_history_or_lifecycle_actions()
    {
        var submitted = new List<string>();
        var provider = Provider(request =>
        {
            Assert.DoesNotContain("/stop", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
                return JsonResponse(Agent());
            Assert.Equal("/v1/agents/agent-1/messages", request.RequestUri.AbsolutePath);
            submitted.Add(Body(request).GetProperty("content").GetString()!);
            return JsonResponse(Run("done", "answer", $"u{submitted.Count}"));
        });
        await provider.ExecuteUnifiedAsync(Request());
        await provider.ExecuteUnifiedAsync(Request(text: "now export as PDF"));
        Assert.Equal(["latest", "now export as PDF"], submitted);
    }

    [Fact]
    public async Task Native_sse_subscribes_before_dispatch_handles_stale_idle_and_deduplicates_updates()
    {
        var subscribed = false;
        var gets = 0;
        var provider = Provider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/events"))
            {
                subscribed = true;
                return Sse("""
                    event: ready
                    data: {"agent_id":"agent-1"}

                    event: status
                    data: {"status":"idle","text":null}

                    event: delta
                    data: {"message_id":"a1",
                    data: "text":"Hello "}

                    event: message
                    data: {"id":"t1","role":"tool","content":"result","tool":{"name":"browser","ok":true}}

                    event: computer
                    data: {"status":"running"}

                    event: message.updated
                    data: {"id":"a1","role":"assistant","content":"Hello **world**"}

                    event: status
                    data: {"status":"idle","text":null}

                    """.Replace("\n", "\r\n") + "\r\n");
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.True(subscribed);
                Assert.False(Body(request).GetProperty("wait").GetBoolean());
                return JsonResponse(Run("working"), HttpStatusCode.Accepted);
            }
            if (path.EndsWith("/messages"))
                return JsonResponse(new { data = new[] { Message("a1", "assistant", "Hello **world**") } });
            return JsonResponse(Agent(++gets == 2 ? "working" : "idle"));
        });
        var events = await Collect(provider.StreamUnifiedAsync(Request()));
        Assert.Equal("Hello **world**", string.Concat(events.Select(part => part.Event.Data).OfType<AITextDeltaEventData>().Select(part => part.Delta)));
        Assert.Single(events, part => part.Event.Type == "text-start");
        Assert.Single(events, part => part.Event.Type == "text-end");
        Assert.Contains(events, part => part.Event.Type == "data-baguettebot-computer");
        Assert.Contains(events, part => part.Event.Type == "data-baguettebot-message" && part.Event.Id == "t1");
        Assert.DoesNotContain(events, part => part.Event.Type.StartsWith("tool-input"));
        var finish = Assert.IsType<AIFinishEventData>(events[^1].Event.Data);
        Assert.Equal("stop", finish.FinishReason);
        Assert.Contains("agent-1", JsonSerializer.Serialize(finish.MessageMetadata));
    }

    [Fact]
    public async Task Screen_and_message_revision_are_raw_not_invented_text_deltas()
    {
        var provider = StreamProvider("""
            event: delta
            data: {"message_id":"a1","text":"draft"}

            event: message.updated
            data: {"id":"a1","role":"assistant","content":"revised"}

            """, finalText: "revised");
        var events = await Collect(provider.StreamUnifiedAsync(Request()));
        Assert.Equal("draft", string.Concat(events.Select(part => part.Event.Data).OfType<AITextDeltaEventData>().Select(part => part.Delta)));
        Assert.Contains(events, part => part.Event.Type == "data-baguettebot-message-updated"
            && Assert.IsType<AIDataEventData>(part.Event.Data).Data is JsonElement raw && raw.GetProperty("content").GetString() == "revised");
        Assert.Equal("revised", Assert.Single(events[^1].Event.Output!.Items!).Content!.OfType<AITextContentPart>().Single().Text);
    }

    [Fact]
    public async Task Heartbeat_reconciles_completion_and_eof_does_not_fake_success()
    {
        var events = await Collect(StreamProvider(": ping\n\n", "done").StreamUnifiedAsync(Request()));
        Assert.Equal("finish", events[^1].Event.Type);
        await Assert.ThrowsAsync<IOException>(() => Collect(StreamProvider("", "pending", stillWorking: true).StreamUnifiedAsync(Request())));
    }

    [Fact]
    public async Task Blocked_task_is_not_success_and_native_notices_survive()
    {
        var provider = StreamProvider("""
            event: message
            data: {"id":"e1","role":"event","content":"insufficient_balance"}

            event: status
            data: {"status":"blocked","text":"insufficient_balance"}

            """, "partial", blocked: true);
        var events = await Collect(provider.StreamUnifiedAsync(Request()));
        Assert.Contains(events, part => part.Event.Type == "error");
        Assert.Equal("other", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
    }

    [Theory]
    [InlineData(402, "insufficient_balance")]
    [InlineData(409, "computer_not_running")]
    [InlineData(429, "rate_limit_exceeded")]
    public async Task Native_errors_retain_http_status_code_message_and_retry_after(int status, string code)
    {
        var provider = Provider(_ =>
        {
            var error = JsonResponse(new { error = new { code, message = "Erreur native" } }, (HttpStatusCode)status);
            error.Headers.TryAddWithoutValidation("Retry-After", "15");
            return error;
        });
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => provider.ExecuteUnifiedAsync(Request(new { operation = "computer" })));
        Assert.Equal((HttpStatusCode)status, exception.StatusCode);
        Assert.Contains(code, exception.Message);
        Assert.Contains("Erreur native", exception.Message);
        Assert.Equal("15", exception.Data["Retry-After"]);
    }

    [Theory]
    [InlineData("agent-1/../other")]
    [InlineData("agent%2fother")]
    [InlineData("..")]
    public async Task Agent_routes_reject_traversal_before_http(string id)
        => await Assert.ThrowsAsync<ArgumentException>(() => Provider(_ => throw new InvalidOperationException())
            .ExecuteUnifiedAsync(Request(model: "baguettebot/agent/" + id)));

    [Fact]
    public async Task Explicit_selection_requires_matching_underlying_model_and_idle_agent()
    {
        var provider = Provider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return JsonResponse(Agent());
        });
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(model: "baguettebot/model/anthropic/claude-x")));
        Assert.Equal("completed", (await Provider(request => request.Method == HttpMethod.Get ? JsonResponse(Agent()) : JsonResponse(Run("done", "ok")))
            .ExecuteUnifiedAsync(Request(new { agent_id = "agent-1" }, model: "baguettebot/model/anthropic/claude-x"))).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(new { agent_id = "agent-1" }, model: "baguettebot/model/different")));
        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(new { agent_id = "agent-2" })));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(_ => JsonResponse(Agent("working"))).ExecuteUnifiedAsync(Request()));
    }

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("computer_start")]
    [InlineData("computer_stop")]
    public async Task Unmapped_lifecycle_operations_do_not_send_requests(string operation)
        => await Assert.ThrowsAsync<NotSupportedException>(() => Provider(_ => throw new InvalidOperationException()).ExecuteUnifiedAsync(Request(new { operation })));

    [Fact]
    public async Task Explicit_stop_does_not_dispatch_or_delete_and_allows_empty_native_response()
    {
        var provider = Provider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1/agents/agent-1/stop", request.RequestUri!.AbsolutePath);
            Assert.Null(request.Content);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("") };
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { operation = "stop" }));
        Assert.Equal("baguettebot_stop", Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AIToolCallContentPart>()).ToolName);
    }

    [Fact]
    public async Task Inline_uploads_are_latest_turn_only_and_downloads_are_inline_authenticated_bytes()
    {
        var uploads = 0;
        var provider = Provider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/files") && request.Method == HttpMethod.Post)
            {
                uploads++;
                Assert.Equal("~/Uploads/invoice.pdf", Body(request).GetProperty("path").GetString());
                Assert.Equal("AQID", Body(request).GetProperty("content_base64").GetString());
                return new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent("") };
            }
            if (path.EndsWith("/files"))
            {
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Contains("path=", request.RequestUri.Query);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) };
            }
            return request.Method == HttpMethod.Get ? JsonResponse(Agent()) : JsonResponse(Run("done", "file saved"));
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { download_paths = new[] { "~/output.pdf" } },
            files: [new AIFileContentPart { Type = "file", Filename = "~/Uploads/invoice.pdf", Data = "data:application/pdf;base64,AQID" }]));
        Assert.Equal(1, uploads);
        var file = Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AIFileContentPart>());
        Assert.Equal("data:application/octet-stream;base64,AQID", file.Data);
        Assert.DoesNotContain("api.baguettebot.com", JsonSerializer.Serialize(file));
    }

    [Fact]
    public async Task Url_inputs_are_never_downloaded_and_live_screen_links_are_preserved_without_key()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() => Provider(_ => throw new InvalidOperationException())
            .ExecuteUnifiedAsync(Request(files: [new AIFileContentPart { Type = "file", Filename = "~/input.pdf", Data = "https://example.com/input.pdf" }])));
        var live = "https://api.baguettebot.com/v1/live/signed?token=expiring";
        var provider = Provider(request =>
        {
            Assert.Equal("/v1/agents/agent-1/computer/live", request.RequestUri!.AbsolutePath);
            Assert.True(Body(request).GetProperty("interactive").GetBoolean());
            return JsonResponse(new { @object = "live_view", url = live, expires_at = "2026-11-01T21:00:00Z", interactive = true });
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { operation = "live", live_view = new { interactive = true, ttl_seconds = 60 } }));
        var serialized = JsonSerializer.Serialize(response);
        Assert.Contains(live, serialized);
        Assert.DoesNotContain("test-key", serialized);
    }

    [Fact]
    public async Task Cancellation_of_async_following_does_not_stop_agent_or_computer()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = new List<string>();
        var provider = Provider(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                cancellation.Cancel();
                return JsonResponse(Run("working"), HttpStatusCode.Accepted);
            }
            return JsonResponse(Agent());
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(), cancellation.Token));
        Assert.DoesNotContain(requests, path => path.EndsWith("/stop") || path.EndsWith("/computer/stop"));
    }

    [Fact]
    public async Task Stream_disposal_and_cancellation_never_send_stop_requests()
    {
        var paths = new List<string>();
        var provider = Provider(request =>
        {
            paths.Add(request.RequestUri!.AbsolutePath);
            if (request.RequestUri.AbsolutePath.EndsWith("/events"))
                return Sse("event: ready\ndata: {\"agent_id\":\"agent-1\"}\n\n");
            return request.Method == HttpMethod.Get ? JsonResponse(Agent()) : JsonResponse(Run("working"), HttpStatusCode.Accepted);
        });
        using var cancellation = new CancellationTokenSource();
        await using (var events = provider.StreamUnifiedAsync(Request(), cancellation.Token).GetAsyncEnumerator())
        {
            Assert.True(await events.MoveNextAsync());
            cancellation.Cancel();
        }
        Assert.Equal(["/v1/agents/agent-1", "/v1/agents/agent-1/events", "/v1/agents/agent-1/messages"], paths);
    }

    [Fact]
    public async Task Observer_timeout_leaves_long_running_task_alone()
    {
        var posted = false;
        var provider = Provider(request =>
        {
            Assert.DoesNotContain("/stop", request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Post)
            {
                posted = true;
                return JsonResponse(Run("working"), HttpStatusCode.Accepted);
            }
            return JsonResponse(Agent(posted ? "working" : "idle"));
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ExecuteUnifiedAsync(Request(new { timeout_seconds = 1, poll_interval_ms = 100 })));
        Assert.True(posted);
    }

    [Fact]
    public async Task History_paginates_without_truncating_tool_results()
    {
        var pages = 0;
        var provider = Provider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("/v1/agents/agent-1/messages", request.RequestUri!.AbsolutePath);
            pages++;
            if (pages == 1)
            {
                Assert.Contains("after=start", request.RequestUri.Query);
                return JsonResponse(new { data = Enumerable.Range(1, 200).Select(index => Message($"m{index}", "tool", $"result {index}")).ToArray() });
            }
            Assert.Contains("after=m200", request.RequestUri.Query);
            return JsonResponse(new { data = new[] { Message("m201", "assistant", "done") } });
        });
        var response = await provider.ExecuteUnifiedAsync(Request(new { operation = "history", after = "start" }));
        Assert.Equal(201, Assert.IsType<JsonElement>(response.Metadata!["baguettebot.raw"]).GetProperty("data").GetArrayLength());
        Assert.Equal(2, pages);
    }

    [Theory]
    [InlineData("""{"wait":"true"}""")]
    [InlineData("""{"timeout_seconds":0}""")]
    [InlineData("""{"agent_id":42}""")]
    [InlineData("""{"download_paths":"~/file"}""")]
    [InlineData("""{"live_view":true}""")]
    public async Task Invalid_provider_controls_fail_before_dispatch(string json)
    {
        using var options = JsonDocument.Parse(json);
        await Assert.ThrowsAsync<ArgumentException>(() => Provider(_ => throw new InvalidOperationException()).ExecuteUnifiedAsync(Request(options.RootElement)));
    }

    [Fact]
    public async Task Unsupported_chat_controls_are_not_silently_emulated()
        => await Assert.ThrowsAsync<NotSupportedException>(() => Provider(_ => throw new InvalidOperationException()).ExecuteUnifiedAsync(new AIRequest
        {
            ProviderId = "baguettebot", Model = "baguettebot/agent/agent-1", Instructions = "do not patch the agent",
            Input = new AIInput { Text = "task" }
        }));

    [Fact]
    public async Task Stream_rejects_unready_or_wrong_agent_without_dispatch()
    {
        await Assert.ThrowsAsync<IOException>(() => Collect(Provider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri!.AbsolutePath.EndsWith("/events") ? Sse(": ping\n\n") : JsonResponse(Agent());
        }).StreamUnifiedAsync(Request())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Collect(Provider(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            return request.RequestUri!.AbsolutePath.EndsWith("/events")
                ? Sse("event: ready\ndata: {\"agent_id\":\"other\"}\n\n") : JsonResponse(Agent());
        }).StreamUnifiedAsync(Request())));
    }

    [Fact]
    public async Task Chat_completion_wrapper_uses_existing_raw_provider_metadata_mapping()
    {
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(Agent());
            Assert.Equal("preserved", Body(request).GetProperty("future_option").GetString());
            return JsonResponse(Run("done", "**Markdown**"));
        });
        var options = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"baguettebot/agent/agent-1","messages":[{"role":"user","content":"latest"}],
             "providerMetadata":{"baguettebot":{"future_option":"preserved"}}}
            """, JsonSerializerOptions.Web)!;
        var serialized = JsonSerializer.Serialize(await provider.CompleteChatAsync(options), JsonSerializerOptions.Web);
        Assert.Contains("**Markdown**", serialized);
        Assert.Contains("agent-1", serialized);
        Assert.Contains("user-1", serialized);
        Assert.Contains("provider_metadata", serialized);
    }

    [Theory]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Protocol_wrappers_preserve_provider_options_and_report_unmapped_controls(string protocol)
    {
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Get)
                return JsonResponse(Agent());
            var body = Body(request);
            Assert.Equal("native", body.GetProperty("future_option").GetString());
            Assert.False(body.TryGetProperty("max_output_tokens", out _));
            return JsonResponse(Run("done", "**result**"));
        });
        string serialized;
        if (protocol == "responses")
        {
            var request = JsonSerializer.Deserialize<ResponseRequest>("""
                {"model":"baguettebot/agent/agent-1","input":"latest","max_output_tokens":100,
                 "metadata":{"baguettebot":{"future_option":"native"}}}
                """, JsonSerializerOptions.Web)!;
            serialized = JsonSerializer.Serialize(await provider.ResponsesAsync(request));
        }
        else
        {
            var request = JsonSerializer.Deserialize<MessagesRequest>("""
                {"model":"baguettebot/agent/agent-1","max_tokens":100,"messages":[{"role":"user","content":"latest"}],
                 "metadata":{"baguettebot":{"future_option":"native"}}}
                """, JsonSerializerOptions.Web)!;
            serialized = JsonSerializer.Serialize(await provider.MessagesAsync(request, []));
        }
        Assert.Contains("**result**", serialized);
        Assert.Contains("agent-1", serialized);
        Assert.Contains("baguettebot.unsupported_options", serialized);
    }

    [Fact]
    public async Task UI_wrapper_accepts_its_required_temperature_without_inventing_sampling_support()
    {
        var provider = StreamProvider("event: delta\ndata: {\"message_id\":\"a1\",\"text\":\"done\"}\n\n", "done");
        var request = JsonSerializer.Deserialize<ChatRequest>("""
            {"id":"ui-1","model":"baguettebot/agent/agent-1","messages":[{"id":"u1","role":"user",
             "parts":[{"type":"text","text":"latest"}]}],"providerMetadata":{"baguettebot":{"future_option":null}}}
            """, JsonSerializerOptions.Web)!;
        var parts = new List<UIMessagePart>();
        await foreach (var part in provider.StreamAsync(request))
            parts.Add(part);
        var serialized = JsonSerializer.Serialize(parts);
        Assert.Contains("done", serialized);
        Assert.Contains("baguettebot.unsupported_options", serialized);
        Assert.Contains("temperature", serialized);
    }

    [Theory]
    [InlineData("chat")]
    [InlineData("responses")]
    [InlineData("messages")]
    public async Task Async_protocol_responses_do_not_claim_successful_completion(string protocol)
    {
        var provider = Provider(request => request.Method == HttpMethod.Get ? JsonResponse(Agent()) : JsonResponse(Run("working"), HttpStatusCode.Accepted));
        if (protocol == "chat")
        {
            var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
                {"model":"baguettebot/agent/agent-1","messages":[{"role":"user","content":"latest"}],
                 "provider_metadata":{"baguettebot":{"wait":false}}}
                """, JsonSerializerOptions.Web)!;
            var response = await provider.CompleteChatAsync(request);
            Assert.Empty(response.Choices);
            Assert.Contains("working", JsonSerializer.Serialize(response));
        }
        else if (protocol == "responses")
        {
            var request = JsonSerializer.Deserialize<ResponseRequest>("""
                {"model":"baguettebot/agent/agent-1","input":"latest","metadata":{"baguettebot":{"wait":false}}}
                """, JsonSerializerOptions.Web)!;
            Assert.Equal("in_progress", (await provider.ResponsesAsync(request)).Status);
        }
        else
        {
            var request = JsonSerializer.Deserialize<MessagesRequest>("""
                {"model":"baguettebot/agent/agent-1","max_tokens":100,"messages":[{"role":"user","content":"latest"}],
                 "metadata":{"baguettebot":{"wait":false}}}
                """, JsonSerializerOptions.Web)!;
            Assert.Equal("pause_turn", (await provider.MessagesAsync(request, [])).StopReason);
        }
    }

    [Fact]
    public async Task Blocked_synchronous_run_returns_failed_unified_status()
    {
        var posted = false;
        var provider = Provider(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                posted = true;
                return JsonResponse(Run("working"), HttpStatusCode.Accepted);
            }
            return request.RequestUri!.AbsolutePath.EndsWith("/messages")
                ? JsonResponse(new { data = new[] { Message("e1", "event", "insufficient_balance") } })
                : JsonResponse(Agent(posted ? "blocked" : "idle"));
        });
        var response = await provider.ExecuteUnifiedAsync(Request());
        Assert.Equal("failed", response.Status);
        Assert.Equal("blocked", response.Metadata!["baguettebot.status"]);
        Assert.Contains("insufficient_balance", JsonSerializer.Serialize(response));
    }

    [Fact]
    public async Task Chat_stream_adapter_preserves_native_events_without_mistaking_runs_for_chat_chunks()
    {
        var provider = StreamProvider("""
            event: computer
            data: {"status":"running"}

            event: delta
            data: {"message_id":"a1","text":"done"}

            """, "done");
        var request = JsonSerializer.Deserialize<ChatCompletionOptions>("""
            {"model":"baguettebot/agent/agent-1","messages":[{"role":"user","content":"latest"}]}
            """, JsonSerializerOptions.Web)!;
        var updates = new List<ChatCompletionUpdate>();
        await foreach (var part in provider.CompleteChatStreamingAsync(request))
            updates.Add(part);
        Assert.All(updates, update => Assert.Equal("chat.completion.chunk", update.Object));
        Assert.All(updates, update => Assert.Equal(request.Model, update.Model));
        Assert.All(updates.SkipLast(1).SelectMany(update => update.Choices), choice => Assert.Null(choice.FinishReason));
        Assert.Contains("running", JsonSerializer.Serialize(updates));
        Assert.Contains("user-1", JsonSerializer.Serialize(updates));
        Assert.Contains("provider_metadata", JsonSerializer.Serialize(updates));
    }

    private static AIRequest Request(object? options = null, string text = "latest", string model = "baguettebot/agent/agent-1",
        List<AIFileContentPart>? files = null)
        => new()
        {
            ProviderId = "baguettebot", Model = model,
            Input = new AIInput
            {
                Items =
                [
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = "old" },
                        new AIFileContentPart { Type = "file", Filename = "old.pdf", Data = "https://never-fetch.example/old.pdf" }] },
                    new AIInputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "old answer" }] },
                    new AIInputItem { Role = "user", Content = [new AITextContentPart { Type = "text", Text = text }, ..files ?? []] }
                ]
            },
            Metadata = options is null ? null : new() { ["baguettebot"] = options }
        };

    private static object Agent(string status = "idle") => new
    {
        id = "agent-1", name = "Assistant", model = "anthropic/claude-x", status,
        status_text = status == "blocked" ? "insufficient_balance" : null, created_at = "2026-10-22T09:12:03.000Z"
    };
    private static object Run(string status, string? output = null, string messageId = "user-1", object[]? messages = null)
        => new { @object = "run", agent_id = "agent-1", message_id = messageId, status, output, messages = messages ?? [] };
    private static object Message(string id, string role, string content) => new { id, role, content };
    private static string TextOutput(AIResponse response)
        => Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AITextContentPart>()).Text;
    private static JsonElement Body(HttpRequestMessage request)
    {
        using var document = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return document.RootElement.Clone();
    }
    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(JsonSerializer.Serialize(value, JsonSerializerOptions.Web), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Sse(string text)
        => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/event-stream") };
    private static BaguetteBotProvider StreamProvider(string frames, string finalText, bool stillWorking = false, bool blocked = false)
    {
        var dispatched = false;
        return Provider(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/events"))
                return Sse("event: ready\ndata: {\"agent_id\":\"agent-1\"}\n\n" + frames + "\n");
            if (request.Method == HttpMethod.Post)
            {
                dispatched = true;
                return JsonResponse(Run("working"), HttpStatusCode.Accepted);
            }
            if (path.EndsWith("/messages"))
                return JsonResponse(new { data = new[] { Message("a1", "assistant", finalText) } });
            return JsonResponse(Agent(dispatched ? stillWorking ? "working" : blocked ? "blocked" : "idle" : "idle"));
        });
    }
    private static async Task<List<AIStreamEvent>> Collect(IAsyncEnumerable<AIStreamEvent> stream)
    {
        var result = new List<AIStreamEvent>();
        await foreach (var part in stream)
            result.Add(part);
        return result;
    }
    private static BaguetteBotProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> handler, string? key = "test-key")
        => new(new KeyResolver(key), new ClientFactory(new HttpClient(new Handler(handler))));
    private sealed class KeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }
    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}
