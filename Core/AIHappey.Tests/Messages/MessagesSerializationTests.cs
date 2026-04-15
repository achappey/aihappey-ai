using System.Text.Json;
using AIHappey.Messages;
using AIHappey.Tests.TestInfrastructure;

namespace AIHappey.Tests.Messages;

public sealed class MessagesSerializationTests
{
    private const string TypedFixturePath = "Fixtures/messages/typed/basic-messages-stream.json";

    [Fact]
    public void Stream_parts_omit_null_properties_when_serialized_with_web_defaults()
    {
        var parts = FixtureFileLoader.LoadMessageTypedFixture(TypedFixturePath);

        var messageStartJson = JsonSerializer.Serialize(parts[0], JsonSerializerOptions.Web);
        Assert.DoesNotContain("\"index\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"content_block\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"delta\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"container\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_details\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_reason\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_sequence\":null", messageStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"metadata\":null", messageStartJson, StringComparison.Ordinal);

        var contentBlockStartJson = JsonSerializer.Serialize(parts[1], JsonSerializerOptions.Web);
        Assert.DoesNotContain("\"message\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"delta\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"usage\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"error\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"citations\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"source\":null", contentBlockStartJson, StringComparison.Ordinal);
        Assert.Contains("\"content_block\":{\"type\":\"text\",\"text\":\"\"}", contentBlockStartJson, StringComparison.Ordinal);

        var messageDeltaJson = JsonSerializer.Serialize(parts[6], JsonSerializerOptions.Web);
        Assert.DoesNotContain("\"type\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"text\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"thinking\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"signature\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"partial_json\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"citation\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_sequence\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cache_creation\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inference_geo\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"server_tool_use\":null", messageDeltaJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"service_tier\":null", messageDeltaJson, StringComparison.Ordinal);
    }

    [Fact]
    public void Messages_response_omits_null_properties_when_serialized_with_web_defaults()
    {
        var response = new MessagesResponse
        {
            Id = "msg_serialization_test",
            Content = [],
            Model = "claude-haiku-test",
            Role = "assistant",
            Type = "message",
            Usage = new MessagesUsage
            {
                InputTokens = 9,
                OutputTokens = 2,
                CacheCreationInputTokens = 0,
                CacheReadInputTokens = 0
            }
        };

        var json = JsonSerializer.Serialize(response, JsonSerializerOptions.Web);

        Assert.Contains("\"id\":\"msg_serialization_test\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"container\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_details\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_reason\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stop_sequence\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"metadata\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"cache_creation\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"inference_geo\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"server_tool_use\":null", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"service_tier\":null", json, StringComparison.Ordinal);
    }
}
