using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Interactions;

public sealed class InteractionsClientToolReplayTests
{
    [Fact]
    public void Four_call_chat_continuation_preserves_model_batch_before_client_results()
    {
        var request = FourCallChatRequest();
        // Exercise the JSON contract used by /api/chat, not just typed unified input.
        var chat = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        var interaction = chat.ToUnifiedRequest("google").ToInteractionRequest("google");
        var steps = interaction.Input!.Steps!.ToArray();

        Assert.Equal(new[] { "user_input", "thought", "function_call", "function_call", "function_call", "function_call",
            "function_result", "function_result", "function_result", "function_result" }, steps.Select(s => s.Type));
        Assert.Equal("opaque-batch-signature", Assert.IsType<InteractionThoughtContent>(steps[1]).Signature);
        Assert.Equal("Find the latest news.", Assert.IsType<InteractionTextContent>(Assert.Single(Assert.IsType<InteractionThoughtContent>(steps[1]).Summary!)).Text);

        var calls = steps.OfType<InteractionFunctionCallContent>().ToArray();
        var results = steps.OfType<InteractionFunctionResultContent>().ToArray();
        Assert.Equal(new[] { "call_1", "call_2", "call_3", "call_4" }, calls.Select(c => c.Id));
        Assert.Equal(calls.Select(c => c.Id), results.Select(r => r.CallId));
        Assert.Equal(new[] { "search_news", "search_news", "search_news", "read_resource" }, calls.Select(c => c.Name));
        Assert.Equal("HR news", JsonSerializer.SerializeToElement(calls[0].Arguments).GetProperty("query").GetString());
        Assert.Equal("HR updates", JsonSerializer.SerializeToElement(calls[1].Arguments).GetProperty("query").GetString());
        Assert.Equal("https://example.test/hr", JsonSerializer.SerializeToElement(calls[3].Arguments).GetProperty("uri").GetString());
        for (var i = 0; i < results.Length; i++)
        {
            Assert.Equal(calls[i].Name, results[i].Name);
            var blocks = Assert.IsAssignableFrom<IEnumerable<InteractionContent>>(results[i].Result);
            Assert.Equal($"result-{i + 1}", Assert.IsType<InteractionTextContent>(Assert.Single(blocks)).Text);
        }
        var body = JsonSerializer.Serialize(interaction, InteractionJson.Default);
        Assert.DoesNotContain("approval", body, StringComparison.OrdinalIgnoreCase);
        Assert.False(interaction.Store);
        Assert.Null(interaction.PreviousInteractionId);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Unified_completed_tools_with_empty_arguments_are_batched_by_id(int count)
    {
        var request = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items = [new AIInputItem
                {
                    Role = "assistant",
                    Content = Enumerable.Range(1, count).Select(i => (AIContentPart)new AIToolCallContentPart
                    {
                        Type = "tool-search_news", ToolCallId = $"call_{i}", ToolName = "search_news",
                        Input = new { }, Output = new { answer = i }, ProviderExecuted = false
                    }).ToList()
                }]
            }
        };
        var steps = request.ToInteractionRequest("google").Input!.Steps!.ToArray();
        Assert.Equal(Enumerable.Repeat("function_call", count).Concat(Enumerable.Repeat("function_result", count)), steps.Select(s => s.Type));
        Assert.Equal(steps.OfType<InteractionFunctionCallContent>().Select(c => c.Id), steps.OfType<InteractionFunctionResultContent>().Select(r => r.CallId));
        Assert.All(steps.OfType<InteractionFunctionCallContent>(), c => Assert.Empty(JsonSerializer.SerializeToElement(c.Arguments).EnumerateObject()));
    }

    [Theory]
    [InlineData("step-start")]
    [InlineData("start-step")]
    public void Historical_batches_in_one_chat_message_do_not_cross_model_step_boundaries(string marker)
    {
        UIMessagePart Boundary() => marker == "step-start" ? new StepStartUIPart() : new StartStepUIPart();
        var chat = FourCallChatRequest();
        var assistant = chat.Messages[1];
        assistant.Parts =
        [
            Boundary(), assistant.Parts[0], assistant.Parts[1], assistant.Parts[2],
            Boundary(), new TextUIPart { Text = "Use the previous results." }, assistant.Parts[3], assistant.Parts[4],
            Boundary(), new TextUIPart { Text = "Finished." }
        ];
        var steps = chat.ToUnifiedRequest("google").ToInteractionRequest("google").Input!.Steps!.ToArray();
        Assert.Equal(new[] { "user_input", "thought", "function_call", "function_call", "function_result", "function_result",
            "model_output", "function_call", "function_call", "function_result", "function_result", "model_output" }, steps.Select(s => s.Type));
        Assert.Equal("Use the previous results.", Assert.IsType<InteractionTextContent>(Assert.Single(Assert.IsType<InteractionModelOutputStep>(steps[6]).Content!)).Text);
        Assert.Equal("Finished.", Assert.IsType<InteractionTextContent>(Assert.Single(Assert.IsType<InteractionModelOutputStep>(steps[11]).Content!)).Text);
    }

    [Fact]
    public void Results_do_not_cross_messages_and_model_content_keeps_its_original_order()
    {
        var chat = FourCallChatRequest();
        var parts = chat.Messages[1].Parts;
        chat.Messages[1].Parts = [parts[0], parts[1], new TextUIPart { Text = "Also checking updates." }, parts[2]];
        chat.Messages.Add(new UIMessage { Role = Role.user, Parts = [new TextUIPart { Text = "Continue." }] });
        chat.Messages.Add(new UIMessage { Role = Role.assistant, Parts = [parts[3], parts[4]] });
        var steps = chat.ToUnifiedRequest("google").ToInteractionRequest("google").Input!.Steps!.ToArray();
        Assert.Equal(new[] { "user_input", "thought", "function_call", "model_output", "function_call", "function_result", "function_result",
            "user_input", "function_call", "function_call", "function_result", "function_result" }, steps.Select(s => s.Type));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Explicit_native_steps_are_not_reordered_or_duplicated(bool parallel)
    {
        var nativeSteps = new List<InteractionStep>
        {
            new InteractionFunctionCallContent { Id = "a", Name = "lookup", Arguments = new { } },
            new InteractionFunctionCallContent { Id = "b", Name = "lookup", Arguments = new { } },
            new InteractionFunctionResultContent { CallId = "a", Name = "lookup", Result = "first" },
            new InteractionFunctionResultContent { CallId = "b", Name = "lookup", Result = "second" }
        };
        if (!parallel)
            nativeSteps = [nativeSteps[0], nativeSteps[2], nativeSteps[1], nativeSteps[3]];

        var native = new InteractionRequest
        {
            Input = new InteractionsInput(nativeSteps, true)
        };
        var unified = native.ToUnifiedRequest("google");
        // Do not rely solely on the raw native-input shortcut: exercise separate unified items too.
        foreach (var request in new[] { unified, new AIRequest { ProviderId = "google", Input = new AIInput { Items = unified.Input!.Items } } })
        {
            var steps = request.ToInteractionRequest("google").Input!.Steps!.ToArray();
            Assert.Equal(nativeSteps.Select(s => s.Type), steps.Select(s => s.Type));
            Assert.Equal(new[] { "a", "b" }, steps.OfType<InteractionFunctionCallContent>().Select(c => c.Id));
        }
    }

    internal static ChatRequest FourCallChatRequest() => new()
    {
        Model = "google/gemini-4-argon",
        Messages =
        [
            new UIMessage { Id = "user-1", Role = Role.user, Parts = [new TextUIPart { Text = "Find HR news." }] },
            new UIMessage
            {
                Id = "assistant-1", Role = Role.assistant,
                Parts =
                [
                    new ReasoningUIPart
                    {
                        Text = "Find the latest news.",
                        ProviderMetadata = new() { ["google"] = new Dictionary<string, object> { ["type"] = "thought_signature", ["signature"] = "opaque-batch-signature" } }
                    },
                    CompletedTool("call_1", "search_news", new { query = "HR news" }, "result-1"),
                    CompletedTool("call_2", "search_news", new { query = "HR updates" }, "result-2"),
                    CompletedTool("call_3", "search_news", new { query = "HR pages" }, "result-3"),
                    CompletedTool("call_4", "read_resource", new { uri = "https://example.test/hr" }, "result-4")
                ]
            }
        ],
        Tools = [new() { Name = "search_news", InputSchema = new() { Type = "object" } },
            new() { Name = "read_resource", InputSchema = new() { Type = "object" } }]
    };

    private static ToolInvocationPart CompletedTool(string id, string name, object input, string result) => new()
    {
        Type = $"tool-{name}", ToolCallId = id, Input = input, ProviderExecuted = false,
        State = "output-available", Approval = new ToolApproval { Id = id, Approved = true },
        Output = new { content = new[] { new { type = "text", text = result } } }
    };
}
