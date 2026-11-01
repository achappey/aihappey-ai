using System.Net;
using System.Reflection;
using System.Text.Json;
using AIHappey.Core.Providers.Google;
using AIHappey.Interactions;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Google;

public sealed class GoogleEnvironmentIdTests
{
    [Theory]
    [InlineData("typed-env", null, "environment_id", "typed-env")]
    [InlineData("typed-env", "extension-env", "environment_id", "typed-env")]
    [InlineData(null, "extension-env", "environment_id", "extension-env")]
    [InlineData("", "extension-env", "environment_id", "extension-env")]
    [InlineData(" ", "extension-env", "ENVIRONMENT_ID", "extension-env")]
    [InlineData(null, null, "environment_id", null)]
    [InlineData("", null, "environment_id", null)]
    [InlineData(" ", null, "environment_id", null)]
    public void ExtractionPrefersTypedIdAndPreservesExtensionDataFallback(
        string? typedId, string? extensionId, string extensionKey, string? expected)
    {
        var interaction = new Interaction
        {
            EnvironmentId = typedId,
            AdditionalProperties = extensionId is null
                ? null
                : new() { [extensionKey] = JsonSerializer.SerializeToElement(extensionId) }
        };
        var method = typeof(GoogleAIProvider).GetMethod(
            "ExtractGoogleAgentEnvironmentId",
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            types: [typeof(Interaction)],
            modifiers: null)!;

        Assert.Equal(expected, method.Invoke(null, [interaction]));
    }

    [Theory]
    [InlineData(false, "env-1", true)]
    [InlineData(false, "env-1", false)]
    [InlineData(true, "env-1", true)]
    [InlineData(true, "env-1", false)]
    [InlineData(false, null, false)]
    [InlineData(true, null, false)]
    public async Task StreamingContinuationStatePreservesTypedEnvironmentId(
        bool customAgent, string? environmentId, bool environmentAtCreation)
    {
        var model = customAgent ? "google/agents/customer-sentinel" : "google/antigravity-preview-latest";
        var stateName = customAgent ? "google_custom_agent_state" : "google_antigravity_state";
        var responses = new List<HttpResponseMessage>
        {
            GoogleClientToolTests.SseResponse(
                JsonSerializer.Serialize(new
                {
                    event_type = "interaction.created",
                    interaction = new
                    {
                        id = "i-1", status = "in_progress",
                        environment_id = environmentAtCreation ? environmentId : null
                    }
                }),
                JsonSerializer.Serialize(new
                {
                    event_type = "interaction.completed",
                    interaction = new
                    {
                        id = "i-1", status = "completed",
                        environment_id = environmentAtCreation ? null : environmentId
                    }
                }))
        };
        if (environmentId is not null)
        {
            // Two zero-filled blocks form an empty tar archive for the environment download.
            responses.Add(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[1024])
            });
        }

        var handler = new GoogleClientToolTests.RecordingHandler(responses.ToArray());
        var events = await GoogleClientToolTests.Stream(
            GoogleClientToolTests.CreateProvider(handler), GoogleClientToolTests.Request(model));
        var inputs = events.Select(e => e.Event.Data).OfType<AIToolInputAvailableEventData>()
            .Where(data => data.ToolName == stateName).ToList();
        var outputs = events.Select(e => e.Event.Data).OfType<AIToolOutputAvailableEventData>()
            .Where(data => data.ToolName == stateName).ToList();

        if (!customAgent && environmentId is null)
        {
            Assert.Empty(inputs);
            Assert.Empty(outputs);
        }
        else
        {
            var input = Assert.Single(inputs);
            Assert.True(input.ProviderExecuted);
            Assert.Equal(environmentId,
                JsonSerializer.SerializeToElement(input.Input).GetProperty("environment_id").GetString());
            Assert.True(Assert.Single(outputs).ProviderExecuted);
        }

        Assert.Equal(environmentId is null ? 1 : 2, handler.Bodies.Count);
    }
}
