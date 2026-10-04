using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;
using static AIHappey.Tests.Google.GoogleClientToolTests;

namespace AIHappey.Tests.Google;

public sealed class GoogleAgentContinuationToolTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompletedClientResultInStateMessageIsSubmittedWithoutReplayingCall(bool customAgent, bool stream)
    {
        var agent = customAgent ? "customer-sentinel" : "antigravity-preview-latest";
        var stateName = customAgent ? "google_custom_agent_state" : "google_antigravity_state";
        var chat = new ChatRequest
        {
            Model = customAgent ? $"google/agents/{agent}" : $"google/{agent}",
            Messages =
            [
                new UIMessage { Id = "user-1", Role = Role.user, Parts = [new TextUIPart { Text = "old request" }] },
                new UIMessage
                {
                    Id = "assistant-1", Role = Role.assistant,
                    Parts =
                    [
                        new TextUIPart { Text = "old response" },
                        new ToolInvocationPart
                        {
                            Type = "tool-github_rest_countries_search_codes", ToolCallId = "call_1827287",
                            Title = "github_rest_countries_search_codes", ProviderExecuted = false,
                            State = "approval-responded", Input = new { name = "poland" },
                            Output = new { content = Array.Empty<object>(), structuredContent = new { countries = new[] { new { name = "Poland", code = "PL" } } } }
                        },
                        new ToolInvocationPart
                        {
                            Type = $"tool-{stateName}", ToolCallId = "state-1", ProviderExecuted = true,
                            State = "output-available",
                            Output = new { structuredContent = new { interaction_id = "previous-interaction", environment_id = "env-1", agent } }
                        }
                    ]
                }
            ]
        };
        // Match /api/chat's serialized UI history, including approval-responded state.
        var replay = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(chat, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        var request = replay.ToUnifiedRequest("google");
        var handler = new RecordingHandler(stream
            ? [SseResponse("""{"event_type":"interaction.completed","interaction":{"id":"current","status":"completed"}}""")]
            : [JsonResponse(new { id = "current", status = "completed" }), JsonResponse(new { id = "current", status = "completed" })]);
        var provider = CreateProvider(handler);
        if (stream) await Stream(provider, request);
        else await provider.ExecuteUnifiedAsync(request);
        using var document = JsonDocument.Parse(handler.Bodies[0]);
        var payload = document.RootElement;
        Assert.Equal("previous-interaction", payload.GetProperty("previous_interaction_id").GetString());
        Assert.Equal("env-1", payload.GetProperty("environment").GetString());
        var input = payload.GetProperty("input");
        Assert.Equal(JsonValueKind.Array, input.ValueKind);
        var result = Assert.Single(input.EnumerateArray());
        Assert.Equal("function_result", result.GetProperty("type").GetString());
        Assert.Equal("call_1827287", result.GetProperty("call_id").GetString());
        Assert.Contains("Poland", result.GetProperty("result").GetRawText());
        Assert.DoesNotContain("function_call", input.GetRawText());
        Assert.DoesNotContain(stateName, input.GetRawText());
        Assert.DoesNotContain("old request", input.GetRawText());
        Assert.DoesNotContain("old response", input.GetRawText());
    }
}
