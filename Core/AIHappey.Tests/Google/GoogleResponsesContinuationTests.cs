using System.Text.Json;
using AIHappey.Interactions;
using AIHappey.Interactions.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Google;

[Collection("Google Responses streaming")]
public sealed class GoogleResponsesContinuationTests
{
    private const string SearchTool = "mcp_github_rest_countries_search_codes_223eb8ccbbcfd74a";

    [Theory]
    [InlineData(1, false)]
    [InlineData(4, false)]
    [InlineData(1, true)]
    [InlineData(4, true)]
    public async Task ResponsesContinuationRecoversResultNamesByCallIdInOutgoingGooglePayload(int count, bool withSignature)
    {
        var input = new List<object>
        {
            new { type = "message", role = "user", content = new[] { new { type = "input_text", text = "Show Poland" } } }
        };
        if (withSignature)
            input.Add(new
            {
                type = "reasoning", id = "interactions-content-0", summary = Array.Empty<object>(),
                status = "completed",
                content = new[] { new { type = "reasoning_text", text = string.Empty, annotations = (object?)null } },
                encrypted_content = "opaque+google/signature==",
                provider_metadata = new { google = new { type = "thought_signature", signature = "opaque+google/signature==" } }
            });
        var names = new[] { SearchTool, "get_detail", SearchTool, "render_widget" }.Take(count).ToArray();
        for (var index = 0; index < count; index++)
            input.Add(new { type = "function_call", call_id = $"call_{index}", name = names[index], arguments = "{\"name\":\"poland\"}" });
        // Results have no name in the standard Responses contract. Reverse their
        // order so the mapper must correlate IDs rather than zip calls/results.
        for (var index = count - 1; index >= 0; index--)
            input.Add(new
            {
                type = "function_call_output", call_id = $"call_{index}", status = "completed",
                output = JsonSerializer.Serialize(new { content = Array.Empty<object>(), structuredContent = new { countryCode = "PL", resultIndex = index } })
            });
        var request = JsonSerializer.Deserialize<ResponseRequest>(JsonSerializer.Serialize(new
        {
            model = "google/gemini-3.5-flash", input, stream = true,
            tools = names.Distinct().Select(name => new { type = "function", name, parameters = new { type = "object" } })
        }), ResponseJson.Default)!;
        var handler = new GoogleClientToolTests.RecordingHandler(GoogleClientToolTests.SseResponse(
            """{"event_type":"interaction.completed","interaction":{"id":"","status":"completed"}}"""));
        await foreach (var _ in GoogleClientToolTests.CreateProvider(handler).ResponsesStreamingAsync(request)) { }

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var steps = body.RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(new[] { "user_input" }.Concat(withSignature ? new[] { "thought" } : [])
            .Concat(Enumerable.Repeat("function_call", count))
            .Concat(Enumerable.Repeat("function_result", count)), steps.Select(step => step.GetProperty("type").GetString()));
        if (withSignature)
        {
            Assert.Equal("opaque+google/signature==", steps[1].GetProperty("signature").GetString());
            // Responses replay metadata is not part of Google's thought schema.
            // Validate the entire wire object so a successful mock cannot hide
            // the real provider's Unknown parameter 'id' validation failure.
            Assert.Equal(new[] { "signature", "type" }, steps[1].EnumerateObject()
                .Select(property => property.Name).OrderBy(name => name, StringComparer.Ordinal));
        }
        var results = steps.Skip(1 + count + (withSignature ? 1 : 0)).ToArray();
        for (var index = 0; index < count; index++)
        {
            var originalIndex = count - 1 - index;
            Assert.Equal($"call_{originalIndex}", results[index].GetProperty("call_id").GetString());
            Assert.Equal(names[originalIndex], results[index].GetProperty("name").GetString());
            var resultText = Assert.Single(results[index].GetProperty("result").EnumerateArray()).GetProperty("text").GetString();
            using var result = JsonDocument.Parse(resultText!);
            Assert.Equal("PL", result.RootElement.GetProperty("countryCode").GetString());
            Assert.Equal(originalIndex, result.RootElement.GetProperty("resultIndex").GetInt32());
        }
    }

    [Fact]
    public void NameRecoveryPreservesExplicitNamesSignaturesAndDoesNotMutateUnifiedInput()
    {
        var unnamed = ToolResult("call_1", null, "result-signature");
        var explicitResult = ToolResult("call_1", "explicit_result_name");
        var input = new AIInput
        {
            Items =
            [
                new() { Role = "assistant", Content = [ToolCall("call_1", "lookup")] },
                new() { Role = "tool", Content = [unnamed, explicitResult] }
            ]
        };
        var results = new AIRequest { ProviderId = "google", Input = input }.ToInteractionRequest("google")
            .Input!.Steps!.OfType<InteractionFunctionResultContent>().ToArray();
        Assert.Equal(new[] { "lookup", "explicit_result_name" }, results.Select(result => result.Name));
        Assert.Equal("result-signature", results[0].Signature);
        Assert.Null(unnamed.ToolName);
        Assert.Equal("explicit_result_name", explicitResult.ToolName);
    }

    [Fact]
    public void NameRecoveryIsRequestLocalAndDoesNotGuessForUnmatchedOrCaseMismatchedIds()
    {
        var first = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput { Items = [new() { Role = "assistant", Content = [ToolCall("call_1", "lookup")] }] }
        };
        _ = first.ToInteractionRequest("google");
        var second = new AIRequest
        {
            ProviderId = "google",
            Input = new AIInput
            {
                Items =
                [
                    new() { Role = "assistant", Content = [ToolCall("CALL_1", "other_lookup")] },
                    new() { Role = "tool", Content = [ToolResult("call_1", null), ToolResult("unknown", null)] }
                ]
            }
        };
        var results = second.ToInteractionRequest("google").Input!.Steps!.OfType<InteractionFunctionResultContent>();
        Assert.All(results, result => Assert.Null(result.Name));
    }

    private static AIToolCallContentPart ToolCall(string id, string name) => new()
    {
        Type = "function_call", ToolCallId = id, ToolName = name, Input = new { }, ProviderExecuted = false
    };

    private static AIToolCallContentPart ToolResult(string id, string? name, string? signature = null) => new()
    {
        Type = "function_call_output", ToolCallId = id, ToolName = name, Output = new { ok = true }, ProviderExecuted = false,
        Metadata = signature is null ? null : new() { ["google"] = new Dictionary<string, object?> { ["signature"] = signature } }
    };
}
