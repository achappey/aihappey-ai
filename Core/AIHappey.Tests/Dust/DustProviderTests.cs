using System.Net;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Dust;
using AIHappey.HeaderAuth;
using AIHappey.Unified.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace AIHappey.Tests.Dust;

public sealed class DustProviderTests
{
    [Theory]
    [InlineData("chat", false)]
    [InlineData("chat", true)]
    [InlineData("responses", false)]
    [InlineData("responses", true)]
    [InlineData("messages", false)]
    [InlineData("messages", true)]
    public async Task Compatibility_wrappers_preserve_namespaced_model_and_explicit_dust_continuation(
        string protocol, bool streaming)
    {
        const string model = "dust/workspace-1/agent-1";
        var identity = new { workspaceId = "workspace-1", agentId = "agent-1", conversationId = "conversation-1" };
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(ConversationPath + "/conversation-1/messages", request.Uri.AbsolutePath);
                AssertMessage(request.Body!.Value, "latest");
                return JsonResponse(new { message = NativeUser(), agentMessages = new[] { Agent() } });
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            return TerminalStream(Token("done"), Success("done"));
        });
        var provider = CreateProvider(handler);
        var results = new List<object>();
        if (protocol == "chat")
        {
            var options = new AIHappey.ChatCompletions.Models.ChatCompletionOptions
            {
                Model = model,
                Messages = [new() { Role = "user", Content = Element("latest") }],
                AdditionalProperties = new() { ["dust"] = Element(identity) }
            };
            if (streaming)
                await foreach (var item in provider.CompleteChatStreamingAsync(options)) results.Add(item);
            else
                results.Add(await provider.CompleteChatAsync(options));
        }
        else if (protocol == "responses")
        {
            var options = Element(new { model, input = "latest", dust = identity })
                .Deserialize<AIHappey.Responses.ResponseRequest>(JsonSerializerOptions.Web)!;
            if (streaming)
                await foreach (var item in provider.ResponsesStreamingAsync(options)) results.Add(item);
            else
                results.Add(await provider.ResponsesAsync(options));
        }
        else
        {
            var options = new AIHappey.Messages.MessagesRequest
            {
                Model = model, MaxTokens = 32,
                Messages = [new() { Role = "user", Content = new AIHappey.Messages.MessagesContent("latest") }],
                AdditionalProperties = new() { ["dust"] = Element(identity) }
            };
            if (streaming)
                await foreach (var item in provider.MessagesStreamingAsync(options, [])) results.Add(item);
            else
                results.Add(await provider.MessagesAsync(options, []));
        }

        var output = JsonSerializer.Serialize(results, JsonSerializerOptions.Web);
        Assert.Contains("done", output);
        Assert.Contains(model, output);
        Assert.Contains("conversation-1", output);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void Dust_header_key_is_available_through_snapshot_and_resolver()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Dust-Key"] = "  dust-header-key  ";
        var snapshot = new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context });
        var resolver = new HeaderApiKeyResolver(snapshot);

        Assert.Equal("X-Dust-Key", HeaderApiKeyResolver.SupportedProviderHeaders["dust"]);
        Assert.Equal("dust-header-key", snapshot.Resolve("dust"));
        Assert.Equal("dust-header-key", snapshot.ProviderKeys["dust"]);
        Assert.Equal("dust-header-key", resolver.Resolve("DUST"));
        Assert.True(resolver.HasConfiguredKey("dust"));
        Assert.Null(resolver.Resolve("openai"));
    }

    [Fact]
    public async Task Discovery_requires_configured_workspace_and_does_not_invent_workspace_endpoints()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("No discovery without workspace."));

        Assert.Empty(await CreateProvider(handler).ListModels());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Discovery_uses_bearer_auth_and_string_agent_ids_with_key_and_workspace_isolated_cache()
    {
        var cache = new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https", request.Uri.Scheme);
            Assert.Equal("dust.tt", request.Uri.Host);
            Assert.Equal("Bearer", request.Scheme);
            Assert.Contains(request.Key, new[] { "key-one", "key-two" });
            Assert.Contains(request.Uri.AbsolutePath, new[]
            {
                "/api/v1/w/workspace-1/assistant/agent_configurations",
                "/api/v1/w/workspace-2/assistant/agent_configurations"
            });
            return JsonResponse(new
            {
                agentConfigurations = new[]
                {
                    new { id = 123, sId = "agent-1", name = "Builder", description = "Builds things", status = "active" }
                }
            });
        });
        var first = CreateProvider(handler, "key-one", cache, "workspace-1");
        Assert.Equal("dust/workspace-1/agent-1", Assert.Single(await first.ListModels()).Id);
        Assert.Equal("dust/workspace-1/agent-1", Assert.Single(await first.ListModels()).Id);
        var secondKey = CreateProvider(handler, "key-two", cache, "workspace-1");
        Assert.Equal("dust/workspace-1/agent-1", Assert.Single(await secondKey.ListModels()).Id);
        var secondWorkspace = CreateProvider(handler, "key-one", cache, "workspace-2");
        Assert.Equal("dust/workspace-2/agent-1", Assert.Single(await secondWorkspace.ListModels()).Id);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Create_posts_only_latest_user_and_selects_new_agent_message_from_nested_history()
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("test-key", request.Key);
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(ConversationPath, request.Uri.AbsolutePath);
                var body = request.Body!.Value;
                AssertMessage(body.GetProperty("message"), "latest");
                Assert.False(body.GetProperty("blocking").GetBoolean());
                Assert.False(body.GetProperty("skipToolsValidation").GetBoolean());
                Assert.False(body.TryGetProperty("instructions", out _));
                return CreatedResponse(Agent(), Agent("stale-message", "old-user"), Agent("wrong-agent", agentId: "agent-other"));
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            return TerminalStream(
                Token("Hello"),
                Success("Hello"));
        });

        var response = await CreateProvider(handler).ExecuteUnifiedAsync(Request(
            [User("old"), new AIInputItem { Role = "assistant", Content = [new AITextContentPart { Type = "text", Text = "old answer" }] }, User("latest")]));

        Assert.Equal("completed", response.Status);
        var content = Assert.Single(response.Output!.Items!).Content!;
        Assert.Equal("Hello", Assert.Single(content.OfType<AITextContentPart>()).Text);
        var receipt = Assert.Single(content.OfType<AIToolCallContentPart>());
        Assert.Equal("create_dust_conversation", receipt.ToolName);
        Assert.True(receipt.ProviderExecuted);
        var identity = Element(receipt.Output).GetProperty("structuredContent");
        Assert.Equal("workspace-1", identity.GetProperty("workspaceId").GetString());
        Assert.Equal("agent-1", identity.GetProperty("agentId").GetString());
        Assert.Equal("conversation-1", identity.GetProperty("conversationId").GetString());
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Continuation_uses_history_receipt_and_posts_direct_latest_message_without_recreating()
    {
        var history = new AIInputItem
        {
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = "identity-1", ToolName = "create_dust_conversation", ProviderExecuted = true,
                    Output = Element(new
                    {
                        structuredContent = new
                        {
                            workspaceId = "workspace-1", agentId = "agent-1", conversationId = "conversation-1"
                        }
                    })
                }
            ]
        };
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(ConversationPath + "/conversation-1/messages", request.Uri.AbsolutePath);
                var body = request.Body!.Value;
                AssertMessage(body, "continue");
                Assert.False(body.TryGetProperty("message", out _));
                Assert.False(body.GetProperty("blocking").GetBoolean());
                return JsonResponse(new { message = NativeUser(), agentMessages = new[] { Agent() } });
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            return TerminalStream(Success("continued"));
        });

        var response = await CreateProvider(handler).ExecuteUnifiedAsync(Request([User("old"), history, User("continue")]));

        Assert.Equal("continued", Assert.Single(Assert.Single(response.Output!.Items!).Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Stream_emits_incremental_text_and_reasoning_before_terminal_without_reading_past_it()
    {
        var handler = ExecutionHandler(() => TerminalStream(
            Token("<think>", "opening_delimiter"),
            Token("Thinking", "chain_of_thought"),
            Token("</think>", "closing_delimiter"),
            Token("Hel"),
            Token("lo"),
            Success("Hello", "Thinking")));

        var events = await CollectAsync(CreateProvider(handler).StreamUnifiedAsync(Request()));

        Assert.Equal(new[] { "Hel", "lo" }, events.Select(e => e.Event.Data).OfType<AITextDeltaEventData>().Select(e => e.Delta));
        Assert.Equal("Thinking", Assert.Single(events.Select(e => e.Event.Data).OfType<AIReasoningDeltaEventData>()).Delta);
        Assert.Single(events, e => e.Event.Type == "text-start");
        Assert.Single(events, e => e.Event.Type == "text-end");
        Assert.Single(events, e => e.Event.Type == "reasoning-start");
        Assert.Single(events, e => e.Event.Type == "reasoning-end");
        Assert.Equal("finish", events[^1].Event.Type);
        Assert.Equal("stop", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
    }

    [Fact]
    public async Task Native_tools_correlate_function_call_ids_and_preserve_params_and_output()
    {
        var action = new
        {
            id = 17, sId = "action-1", createdAt = 1, updatedAt = 2, agentMessageId = 456,
            internalMCPServerName = "search-server", toolName = "search", mcpServerId = "server-1",
            functionCallName = "search", functionCallId = "call-1", @params = new { query = "Dust" },
            citationsAllocated = 0, status = "succeeded", step = 0, executionDurationMs = 12,
            displayLabels = new { running = "Searching", done = "Searched" },
            generatedFiles = Array.Empty<object>(), output = new[] { new { type = "text", text = "found" } }
        };
        var handler = ExecutionHandler(() => TerminalStream(
            Native(new { type = "tool_call_started", configurationId = "agent-1", messageId = "message-1", toolName = "search", toolCallId = "call-1", toolCallIndex = 0 }),
            Native(new { type = "tool_params", configurationId = "agent-1", messageId = "message-1", action }),
            Native(new { type = "agent_action_success", configurationId = "agent-1", messageId = "message-1", action }),
            Success("done")));

        var events = await CollectAsync(CreateProvider(handler).StreamUnifiedAsync(Request()));

        var started = Assert.Single(events, e => e.Event.Type == "tool-input-start");
        Assert.Equal("call-1", started.Event.Id);
        var input = Assert.Single(events, e => e.Event.Type == "tool-input-available"
            && e.Event.Data is AIToolInputAvailableEventData { ToolName: "search" });
        Assert.Equal("call-1", input.Event.Id);
        var inputData = Assert.IsType<AIToolInputAvailableEventData>(input.Event.Data);
        Assert.True(inputData.ProviderExecuted);
        Assert.Equal("Dust", Element(inputData.Input).GetProperty("query").GetString());
        var output = Assert.Single(events, e => e.Event.Type == "tool-output-available"
            && e.Event.Id == "call-1");
        var outputData = Assert.IsType<AIToolOutputAvailableEventData>(output.Event.Data);
        Assert.True(outputData.ProviderExecuted);
        Assert.Contains("found", JsonSerializer.Serialize(outputData.Output));
        Assert.Equal("finish", events[^1].Event.Type);
    }

    [Theory]
    [InlineData("agent_error")]
    [InlineData("agent_generation_cancelled")]
    public async Task Native_failure_and_cancellation_never_emit_successful_finish(string type)
    {
        object terminal = type == "agent_error"
            ? new { type, error = new { code = "provider_error", message = "Native failure", metadata = new { } } }
            : new { type, status = "cancelled" };
        var handler = ExecutionHandler(() => TerminalStream(Native(terminal)));

        var events = new List<AIStreamEvent>();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var item in CreateProvider(handler).StreamUnifiedAsync(Request()))
                events.Add(item);
        });

        Assert.Contains(type == "agent_error" ? "Native failure" : "cancelled", exception.Message);
        Assert.DoesNotContain(events, e => e.Event.Type == "finish");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Truncated_stream_is_not_reported_as_success(bool insideFrame)
    {
        var frame = Token("partial");
        var handler = ExecutionHandler(() => SseResponse(insideFrame ? frame.TrimEnd() : frame));
        var events = new List<AIStreamEvent>();

        await Assert.ThrowsAnyAsync<IOException>(async () =>
        {
            await foreach (var item in CreateProvider(handler).StreamUnifiedAsync(Request()))
                events.Add(item);
        });

        Assert.DoesNotContain(events, e => e.Event.Type == "finish");
    }

    [Fact]
    public async Task Stream_yields_tokens_before_upstream_finishes_and_honors_cancellation()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var handler = ExecutionHandler(() => StreamResponse(new FrameStream(
            [Token("first")],
            waitAfterFrames: true)));
        await using var enumerator = CreateProvider(handler).StreamUnifiedAsync(Request(), cancellation.Token).GetAsyncEnumerator();
        while (await enumerator.MoveNextAsync())
        {
            if (enumerator.Current.Event.Data is not AITextDeltaEventData delta)
                continue;
            Assert.Equal("first", delta.Delta);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (await enumerator.MoveNextAsync()) { }
            });
            return;
        }
        Assert.Fail("The provider did not yield incremental text.");
    }

    [Fact]
    public async Task Done_reconnects_with_outer_event_cursor_instead_of_finishing()
    {
        var subscription = 0;
        var handler = ExecutionHandler(() =>
        {
            subscription++;
            return subscription == 1
                ? SseResponse(Token("Hello", eventId: "42-0") + "data: done\n\n")
                : TerminalStream(Success("Hello"));
        });

        var events = await CollectAsync(CreateProvider(handler).StreamUnifiedAsync(Request()));

        Assert.Equal(2, subscription);
        Assert.Contains("lastEventId=42-0", handler.Requests[^1].Uri.Query);
        Assert.Single(events.Select(e => e.Event.Data).OfType<AITextDeltaEventData>());
        Assert.Single(events, e => e.Event.Type == "finish");
        Assert.All(handler.Requests, request => Assert.Equal("test-key", request.Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Approval_step_emits_all_requests_before_stopping_at_last_blocking_event(bool? firstIsLast)
    {
        var handler = ExecutionHandler(() => TerminalStream(
            ApprovalFrame("action-first", firstIsLast, "6-0"),
            ApprovalFrame("action-last", true, "7-0")));

        var events = await CollectAsync(CreateProvider(handler).StreamUnifiedAsync(Request()));

        var approvals = events.Select(e => e.Event.Data).OfType<AIToolApprovalRequestEventData>().ToList();
        Assert.Equal(2, approvals.Count);
        Assert.Equal(2, approvals.Select(approval => approval.ApprovalId).Distinct().Count());
        Assert.Equal(2, approvals.Select(approval => approval.ToolCallId).Distinct().Count());
        Assert.Equal("tool-calls", Assert.IsType<AIFinishEventData>(events[^1].Event.Data).FinishReason);
    }

    [Fact]
    public async Task Approvals_without_last_flag_checkpoint_at_done_and_resume_after_all_decisions()
    {
        var validations = new List<string>();
        var subscriptions = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ConversationPath)
                return CreatedResponse(Agent());
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(ConversationPath + "/child-conversation/messages/child-message/validate-action", request.Uri.AbsolutePath);
                validations.Add(request.Body!.Value.GetProperty("actionId").GetString()!);
                Assert.Equal(JsonValueKind.True, request.Body.Value.GetProperty("approved").ValueKind);
                return JsonResponse(new { success = true });
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            subscriptions++;
            if (subscriptions == 1)
                return TerminalStream(ApprovalFrame("action-first", null, "6-0"),
                    ApprovalFrame("action-last", null, "7-0"), "data: done\n\n");
            Assert.Equal(new[] { "action-first", "action-last" }, validations);
            Assert.Contains("lastEventId=7-0", request.Uri.Query);
            return TerminalStream(Token("resumed"), Success("resumed"));
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var provider = CreateProvider(handler);
        var pending = await provider.ExecuteUnifiedAsync(Request(), timeout.Token);
        Assert.Equal("requires_action", pending.Status);
        Assert.Equal(1, subscriptions);
        var approvals = Assert.Single(pending.Output!.Items!).Content!.OfType<AIToolCallContentPart>()
            .Where(tool => tool.State == "approval-requested").ToList();
        Assert.Equal(2, approvals.Count);
        var response = new AIInputItem
        {
            Role = "assistant", Metadata = pending.Metadata,
            Content = approvals.Select(tool => (AIContentPart)new AIToolCallContentPart
            {
                Type = "tool-call", ToolCallId = tool.ToolCallId, ToolName = tool.ToolName,
                ProviderExecuted = true, State = "approval-responded", Input = tool.Input, Metadata = tool.Metadata,
                Approval = new AIToolCallApproval { Id = tool.Approval!.Id, Approved = true }
            }).ToList()
        };

        var resumed = await provider.ExecuteUnifiedAsync(Request([response]), timeout.Token);

        Assert.Equal("completed", resumed.Status);
        Assert.Equal(2, subscriptions);
        Assert.Equal("resumed", Assert.Single(Assert.Single(resumed.Output!.Items!).Content!.OfType<AITextContentPart>()).Text);
    }

    [Fact]
    public async Task Ui_stream_child_approval_history_preserves_parent_identity_and_resume_cursor()
    {
        var subscriptions = 0;
        var validations = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ConversationPath)
            {
                Assert.Equal(0, subscriptions);
                return CreatedResponse(Agent());
            }
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal(ConversationPath + "/child-conversation/messages/child-message/validate-action", request.Uri.AbsolutePath);
                Assert.Equal("action-1", request.Body!.Value.GetProperty("actionId").GetString());
                Assert.Equal(JsonValueKind.True, request.Body.Value.GetProperty("approved").ValueKind);
                validations++;
                return JsonResponse(new { success = true });
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            subscriptions++;
            if (subscriptions == 1)
                return TerminalStream(Token("before approval", eventId: "6-0"), ApprovalFrame("action-1", true, "7-0"));
            Assert.Equal(1, validations);
            Assert.Contains("lastEventId=7-0", request.Uri.Query);
            return TerminalStream(Token("after approval", eventId: "8-0"), Success("after approval"));
        });
        var provider = CreateProvider(handler);
        var request = new AIHappey.Vercel.Models.ChatRequest
        {
            Id = "ui-1", Model = "dust/workspace-1/agent-1",
            Messages = [new() { Id = "user-1", Role = AIHappey.Vercel.Models.Role.user, Parts = [new AIHappey.Vercel.Models.TextUIPart { Text = "latest" }] }]
        };
        var first = new List<AIHappey.Vercel.Models.UIMessagePart>();
        await foreach (var part in provider.StreamAsync(request))
            first.Add(part);
        var approval = Assert.Single(first.OfType<AIHappey.Vercel.Models.ToolApprovalRequestUIPart>());
        var call = Assert.Single(first.OfType<AIHappey.Vercel.Models.ToolCallPart>(),
            part => part.ToolCallId == approval.ToolCallId);
        request.Messages.Add(new()
        {
            Id = "assistant-1", Role = AIHappey.Vercel.Models.Role.assistant,
            Parts =
            [
                new AIHappey.Vercel.Models.ToolInvocationPart
                {
                    Type = "tool-" + call.ToolName, ToolCallId = call.ToolCallId, Input = call.Input,
                    State = "approval-responded", ProviderExecuted = call.ProviderExecuted,
                    CallProviderMetadata = call.ProviderMetadata,
                    Approval = new AIHappey.Vercel.Models.ToolApproval { Id = approval.ApprovalId, Approved = true }
                }
            ]
        });

        var resumed = new List<AIHappey.Vercel.Models.UIMessagePart>();
        await foreach (var part in provider.StreamAsync(request))
            resumed.Add(part);

        Assert.Equal("after approval", string.Concat(resumed.OfType<AIHappey.Vercel.Models.TextDeltaUIMessageStreamPart>().Select(part => part.Delta)));
        Assert.DoesNotContain(resumed, part => part.Type == "tool-approval-request");
        Assert.Equal(2, subscriptions);
        Assert.Equal(1, validations);
        Assert.Single(handler.Requests, recorded => recorded.Method == HttpMethod.Post && recorded.Uri.AbsolutePath == ConversationPath);
    }

    [Fact]
    public async Task Nonstream_output_preserves_text_reasoning_and_tool_source_order()
    {
        var action = new
        {
            sId = "action-1", toolName = "search", functionCallId = "call-1",
            @params = new { query = "Dust" }, status = "succeeded",
            output = new[] { new { type = "text", text = "found" } }
        };
        var handler = ExecutionHandler(() => TerminalStream(
            Token("before "),
            Token("thinking", "chain_of_thought"),
            Native(new { type = "tool_params", configurationId = "agent-1", messageId = "message-1", action }),
            Native(new { type = "agent_action_success", configurationId = "agent-1", messageId = "message-1", action }),
            Token("after"),
            Success("before after", "thinking")));

        var response = await CreateProvider(handler).ExecuteUnifiedAsync(Request());
        var content = Assert.Single(response.Output!.Items!).Content!.Where(part =>
            part is not AIToolCallContentPart { ToolName: "create_dust_conversation" }).ToList();

        Assert.Collection(content,
            part => Assert.Equal("before ", Assert.IsType<AITextContentPart>(part).Text),
            part => Assert.Equal("thinking", Assert.IsType<AIReasoningContentPart>(part).Text),
            part => Assert.Equal("search", Assert.IsType<AIToolCallContentPart>(part).ToolName),
            part => Assert.Equal("after", Assert.IsType<AITextContentPart>(part).Text));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Approval_roundtrip_uses_native_child_route_and_boolean_decision_without_replaying_history(
        bool approved, bool nativeDecision)
    {
        var subscriptions = 0;
        var validations = 0;
        var continuations = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ConversationPath)
                return CreatedResponse(Agent());
            if (request.Uri.AbsolutePath.EndsWith("/validate-action", StringComparison.Ordinal))
            {
                validations++;
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal(ConversationPath + "/child-conversation/messages/child-message/validate-action",
                    request.Uri.AbsolutePath);
                var body = request.Body!.Value;
                Assert.Equal(2, body.EnumerateObject().Count());
                Assert.Equal("action-1", body.GetProperty("actionId").GetString());
                Assert.Equal(nativeDecision ? JsonValueKind.True : JsonValueKind.False, body.GetProperty("approved").ValueKind);
                Assert.Equal(nativeDecision, body.GetProperty("approved").GetBoolean());
                return JsonResponse(new { success = true });
            }
            if (request.Method == HttpMethod.Post)
            {
                continuations++;
                Assert.Equal(ConversationPath + "/conversation-1/messages", request.Uri.AbsolutePath);
                AssertMessage(request.Body!.Value, "next");
                return JsonResponse(new { message = NativeUser(), agentMessages = new[] { Agent() } });
            }
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            subscriptions++;
            if (subscriptions == 2)
                Assert.Contains("lastEventId=7-0", request.Uri.Query);
            return subscriptions == 1
                ? TerminalStream(
                    Token("before approval", eventId: "6-0"),
                    ApprovalFrame("action-1", true, "7-0"))
                : TerminalStream(
                    Token("after approval", eventId: "8-0"),
                    Success("after approval"));
        });
        var provider = CreateProvider(handler);
        var pending = await provider.ExecuteUnifiedAsync(Request());
        Assert.Equal("requires_action", pending.Status);
        var tool = Assert.Single(Assert.Single(pending.Output!.Items!).Content!.OfType<AIToolCallContentPart>(),
            part => part.State == "approval-requested");
        Assert.True(tool.ProviderExecuted);
        Assert.NotNull(tool.Approval?.Id);
        var decision = new AIInputItem
        {
            Role = "assistant",
            Content =
            [
                new AIToolCallContentPart
                {
                    Type = "tool-call",
                    ToolCallId = tool.ToolCallId, ToolName = tool.ToolName, ProviderExecuted = true,
                    State = "approval-responded", Input = tool.Input, Metadata = tool.Metadata,
                    Approval = new AIToolCallApproval { Id = tool.Approval!.Id, Approved = approved }
                }
            ],
            Metadata = pending.Metadata
        };

        var resumed = await provider.ExecuteUnifiedAsync(Request([decision]));
        Assert.Equal("completed", resumed.Status);
        Assert.Equal("after approval", Assert.Single(Assert.Single(resumed.Output!.Items!).Content!.OfType<AITextContentPart>()).Text);
        Assert.Equal(1, validations);
        Assert.Equal(0, continuations);
        await provider.ExecuteUnifiedAsync(Request([decision, User("next")]));
        Assert.Equal(1, validations);
        Assert.Equal(1, continuations);
        Assert.Single(handler.Requests, request =>
            request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ConversationPath);
    }

    [Theory]
    [InlineData("agent-1")]
    [InlineData("dust/workspace-1/agent-1/extra")]
    [InlineData("dust/../agent-1")]
    [InlineData("dust/workspace-1/agent%2Fother")]
    public async Task Malformed_model_routes_are_rejected_before_HTTP(string model)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must reject before HTTP."));
        var request = new AIRequest
        {
            ProviderId = "dust", Model = model, Input = new AIInput { Text = "hello" }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(handler).ExecuteUnifiedAsync(request));

        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("workspace-other", "agent-1")]
    [InlineData("workspace-1", "agent-other")]
    public async Task Conversation_identity_cannot_cross_workspace_or_agent(string workspaceId, string agentId)
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must reject before HTTP."));
        var provider = CreateProvider(handler);

        await Assert.ThrowsAsync<ArgumentException>(() => provider.ExecuteUnifiedAsync(Request(
            metadata: new { workspaceId, agentId, conversationId = "conversation-1" })));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Conflicting_history_and_request_conversation_identity_is_rejected_before_HTTP()
    {
        var handler = new RecordingHandler(_ => throw new InvalidOperationException("Must reject before HTTP."));
        var history = new AIInputItem
        {
            Role = "assistant",
            Metadata = new Dictionary<string, object?>
            {
                ["dust"] = Element(new
                {
                    workspaceId = "workspace-1", agentId = "agent-1", conversationId = "conversation-old"
                })
            }
        };

        await Assert.ThrowsAsync<ArgumentException>(() => CreateProvider(handler).ExecuteUnifiedAsync(
            Request([history, User("latest")], new { conversationId = "conversation-new" })));

        Assert.Empty(handler.Requests);
    }

    private static AIRequest Request(List<AIInputItem>? items = null, object? metadata = null)
        => new()
        {
            Id = "request-1",
            ProviderId = "dust",
            Model = "dust/workspace-1/agent-1",
            Instructions = "System instructions must not be replayed.",
            Input = new AIInput { Items = items ?? [User("latest")] },
            Metadata = metadata is null ? null : new Dictionary<string, object?>
            {
                ["dust"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web)
            }
        };

    [Fact]
    public async Task Public_events_redirect_preserves_authentication_and_reconnect_cursor()
    {
        var subscriptions = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post) return CreatedResponse(Agent());
            Assert.Equal("Bearer", request.Scheme);
            Assert.Equal("test-key", request.Key);
            Assert.Contains("text/event-stream", request.Accept);
            if (request.Uri.AbsolutePath == EventsPath)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("/api/sse" + request.Uri.PathAndQuery[4..], UriKind.Relative);
                return response;
            }
            Assert.Equal("/api/sse" + EventsPath[4..], request.Uri.AbsolutePath);
            subscriptions++;
            if (subscriptions == 1)
            {
                Assert.Empty(request.Uri.Query);
                return SseResponse(Token("Hello", eventId: "7-0") + "data: done\n\n");
            }
            Assert.Equal("?lastEventId=7-0", request.Uri.Query);
            return TerminalStream(Success("Hello"));
        });

        var response = await CreateProvider(handler).ExecuteUnifiedAsync(Request());

        Assert.Equal("completed", response.Status);
        Assert.Equal(2, subscriptions);
        Assert.Equal(5, handler.Requests.Count);
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == EventsPath));
    }

    [Theory]
    [InlineData("https://example.com/api/sse/v1/events")]
    [InlineData("http://dust.tt/api/sse/v1/events")]
    [InlineData("/api/sse/v1/w/workspace-1/assistant/conversations/other/messages/message-1/events")]
    [InlineData("/api/sse/v1/w/workspace-1/assistant/conversations/conversation-1/messages/message-1/events?lastEventId=other")]
    [InlineData("/api/sse/v1/w/workspace-1/assistant/conversations/conversation-1/messages/message-1/events#fragment")]
    [InlineData(null)]
    public async Task Unexpected_events_redirect_is_rejected_before_forwarding_credentials(string? location)
    {
        var handler = ExecutionHandler(() =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            if (location is not null) response.Headers.Location = new Uri(location, UriKind.RelativeOrAbsolute);
            return response;
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(handler).ExecuteUnifiedAsync(Request()));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Events_redirect_is_followed_only_once()
    {
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Post) return CreatedResponse(Agent());
            var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
            response.Headers.Location = new Uri("/api/sse" + EventsPath[4..], UriKind.Relative);
            return response;
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(handler).ExecuteUnifiedAsync(Request()));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData("missing-user")]
    [InlineData("missing-user-id")]
    [InlineData("missing-agent")]
    [InlineData("wrong-parent")]
    [InlineData("wrong-agent")]
    public async Task Creation_rejects_incompatible_conversation_content_even_with_top_level_message(string shape)
    {
        var users = shape == "missing-user" ? Array.Empty<object>()
            : shape == "missing-user-id" ? [new { type = "user_message" }] : new[] { NativeUser() };
        var agents = shape == "missing-agent" ? Array.Empty<object>()
            : new[] { Agent(parent: shape == "wrong-parent" ? "other-user" : "user-1",
                agentId: shape == "wrong-agent" ? "other-agent" : "agent-1") };
        var handler = new RecordingHandler(_ => JsonResponse(new
        {
            conversation = new { sId = "conversation-1", content = new[] { users, agents } },
            message = NativeUser()
        }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(handler).ExecuteUnifiedAsync(Request()));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Approval_retries_only_verified_enum_schema_rejection(bool approved)
    {
        var validations = 0;
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Get)
            {
                Assert.Equal(EventsPath, request.Uri.AbsolutePath);
                Assert.Equal("?lastEventId=7-0", request.Uri.Query);
                return TerminalStream(Success("done"));
            }
            Assert.Equal(ConversationPath + "/child-conversation/messages/child-message/validate-action", request.Uri.AbsolutePath);
            validations++;
            var decision = request.Body!.Value.GetProperty("approved");
            if (validations == 1)
            {
                Assert.Equal(approved ? JsonValueKind.True : JsonValueKind.False, decision.ValueKind);
                var response = JsonResponse(new
                {
                    error = new
                    {
                        type = "invalid_request_error",
                        message = "Invalid request body: Invalid option: expected one of \"approved\"|\"rejected\"|\"always_approved\" at \"approved\""
                    }
                });
                response.StatusCode = HttpStatusCode.BadRequest;
                return response;
            }
            Assert.Equal(JsonValueKind.String, decision.ValueKind);
            Assert.Equal(approved ? "approved" : "rejected", decision.GetString());
            return JsonResponse(new { success = true });
        });

        var response = await CreateProvider(handler).ExecuteUnifiedAsync(ApprovalRequest(approved));
        Assert.Equal("completed", response.Status);
        Assert.Equal(2, validations);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Approval_does_not_retry_other_failures(HttpStatusCode status)
    {
        var handler = new RecordingHandler(request =>
        {
            Assert.Equal(JsonValueKind.True, request.Body!.Value.GetProperty("approved").ValueKind);
            var response = JsonResponse(new { error = new { type = "invalid_request_error", message = "Action is no longer blocked." } });
            response.StatusCode = status;
            return response;
        });

        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(handler).ExecuteUnifiedAsync(ApprovalRequest(true)));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Approval_requires_successful_acknowledgement_before_resuming()
    {
        var handler = new RecordingHandler(_ => JsonResponse(new { success = false }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => CreateProvider(handler).ExecuteUnifiedAsync(ApprovalRequest(true)));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Invalid request body: actionId is missing\"}}")]
    [InlineData("{\"error\":{\"type\":\"invalid_request_error\",\"message\":\"Action approved rejected always_approved is no longer blocked\"}}")]
    [InlineData("{\"error\":{\"type\":\"permission_error\",\"message\":\"Invalid request body: approved rejected always_approved\"}}")]
    public async Task Approval_does_not_retry_incompatible_or_unrelated_validation_errors(string body)
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        });
        await Assert.ThrowsAsync<HttpRequestException>(() => CreateProvider(handler).ExecuteUnifiedAsync(ApprovalRequest(true)));
        Assert.Single(handler.Requests);
    }

    private static AIRequest ApprovalRequest(bool approved)
        => Request(metadata: new
        {
            conversationId = "conversation-1",
            approval = new
            {
                conversationId = "child-conversation", messageId = "child-message", actionId = "action-1",
                streamMessageId = "message-1", lastEventId = "7-0", approved = approved ? "approved" : "rejected"
            }
        });

    private const string ConversationPath = "/api/v1/w/workspace-1/assistant/conversations";
    private const string EventsPath = "/api/v1/w/workspace-1/assistant/conversations/conversation-1/messages/message-1/events";
    private static int _nextEventId = 100;

    private static void AssertMessage(JsonElement message, string content)
    {
        Assert.Equal(content, message.GetProperty("content").GetString());
        Assert.Equal("agent-1", Assert.Single(message.GetProperty("mentions").EnumerateArray())
            .GetProperty("configurationId").GetString());
        Assert.Equal("UTC", message.GetProperty("context").GetProperty("timezone").GetString());
    }

    private static object NativeUser() => new { sId = "user-1", type = "user_message", content = "latest" };

    private static object Agent(string id = "message-1", string parent = "user-1", string agentId = "agent-1",
        string status = "created", string? content = null, string? reasoning = null)
        => new
        {
            id = 123, sId = id, agentMessageId = 456, type = "agent_message", parentMessageId = parent,
            configuration = new { sId = agentId }, status, content, chainOfThought = reasoning, actions = Array.Empty<object>()
        };

    private static HttpResponseMessage CreatedResponse(params object[] agents)
        => JsonResponse(new
        {
            conversation = new { sId = "conversation-1", content = new[] { new[] { NativeUser() }, agents } }
        });

    private static string Native(object data, string? eventId = null)
        => "data: " + JsonSerializer.Serialize(new
        {
            eventId = eventId ?? $"{Interlocked.Increment(ref _nextEventId)}-0", data
        }, JsonSerializerOptions.Web) + "\n\n";

    private static string Success(string content, string? reasoning = null)
        => Native(new { type = "agent_message_success", configurationId = "agent-1", messageId = "message-1", message = Agent(status: "succeeded", content: content, reasoning: reasoning), runIds = Array.Empty<string>() });

    private static string Token(string text, string classification = "tokens", string? eventId = null)
        => Native(new { type = "generation_tokens", created = 1, configurationId = "agent-1", messageId = "message-1", text, classification }, eventId);

    private static string ApprovalFrame(string actionId, bool? last, string eventId)
    {
        var data = Element(new
        {
            type = "tool_approve_execution", created = 1, configurationId = "agent-1",
            conversationId = "child-conversation", messageId = "child-message", actionId,
            inputs = new { query = "search terms" },
            metadata = new { toolName = "search", mcpServerName = "search-server" }
        }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        if (last is not null)
            data["isLastBlockingEventForStep"] = last.Value;
        return Native(data, eventId);
    }

    private static RecordingHandler ExecutionHandler(Func<HttpResponseMessage> stream)
        => new(request =>
        {
            if (request.Method == HttpMethod.Post && request.Uri.AbsolutePath == ConversationPath)
                return CreatedResponse(Agent());
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(EventsPath, request.Uri.AbsolutePath);
            Assert.Equal("Bearer", request.Scheme);
            return stream();
        });

    private static HttpResponseMessage TerminalStream(params string[] frames)
        => StreamResponse(new FrameStream(frames));

    private static HttpResponseMessage StreamResponse(Stream stream)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    private static AIInputItem User(string text)
        => new() { Role = "user", Content = [new AITextContentPart { Type = "text", Text = text }] };

    private static JsonElement Element(object? value)
        => JsonSerializer.SerializeToElement(value, JsonSerializerOptions.Web);

    private static HttpResponseMessage JsonResponse(object body)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web),
                Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage SseResponse(string body)
        => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static async Task<List<AIStreamEvent>> CollectAsync(IAsyncEnumerable<AIStreamEvent> source)
    {
        var events = new List<AIStreamEvent>();
        await foreach (var item in source)
            events.Add(item);
        return events;
    }

    private static DustProvider CreateProvider(RecordingHandler handler, string? key = "test-key",
        AsyncCacheHelper? cache = null, string? workspaceId = null)
    {
        var context = new DefaultHttpContext();
        if (workspaceId is not null)
            context.Request.Headers["X-Dust-Workspace-Id"] = workspaceId;
        return new(new StaticApiKeyResolver(key),
            cache ?? new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
            new StaticHttpClientFactory(new HttpClient(handler)),
            new HttpContextAccessor { HttpContext = context });
    }

    private sealed record RecordedRequest(HttpMethod Method, Uri Uri, string? Scheme, string? Key,
        JsonElement? Body, string[] Accept);

    private sealed class RecordingHandler(Func<RecordedRequest, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var recorded = new RecordedRequest(request.Method, request.RequestUri!,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                body is null ? null : JsonDocument.Parse(body).RootElement.Clone(),
                request.Headers.Accept.Select(value => value.MediaType!).ToArray());
            Requests.Add(recorded);
            return responder(recorded);
        }
    }

    private sealed class StaticApiKeyResolver(string? key) : IApiKeyResolver
    {
        public string? Resolve(string provider) => key;
    }

    private sealed class StaticHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("dust-api", name);
            return client;
        }
    }

    private sealed class FrameStream(IEnumerable<string> frames, bool waitAfterFrames = false) : Stream
    {
        private readonly Queue<byte[]> _frames = new(frames.Select(Encoding.UTF8.GetBytes));
        private byte[]? _current;
        private int _offset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_current is null || _offset == _current.Length)
            {
                if (!_frames.TryDequeue(out _current))
                {
                    if (waitAfterFrames)
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    throw new InvalidOperationException("Provider read beyond the terminal SSE event.");
                }
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
