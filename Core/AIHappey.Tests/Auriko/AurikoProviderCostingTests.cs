using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.Auriko;

namespace AIHappey.Tests.Auriko;

public class AurikoProviderCostingTests
{
    [Fact]
    public void GetGatewayCost_uses_numeric_estimated_cost()
    {
        var usage = UsageElement("""
        {
            "prompt_tokens": 438,
            "total_tokens": 537,
            "completion_tokens": 99,
            "estimated_cost": 0.00004452
        }
        """);

        var cost = AurikoProvider.GetGatewayCost(usage);

        Assert.Equal(0.00004452m, cost);
    }

    [Fact]
    public void GetGatewayCost_uses_string_exponent_estimated_cost()
    {
        var usage = UsageElement("""{ "estimated_cost": "1.23e-7" }""");

        var cost = AurikoProvider.GetGatewayCost(usage);

        Assert.Equal(0.000000123m, cost);
    }

    [Fact]
    public void ChatCompletion_enrichment_preserves_usage_and_writes_gateway_cost()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-auriko-1",
            Created = 1785593130,
            Model = "phi-4",
            Usage = UsageElement("""
            {
                "prompt_tokens": 438,
                "completion_tokens": 99,
                "total_tokens": 537,
                "estimated_cost": 0.00004452
            }
            """),
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["metadata"] = UsageElement("""{ "trace_id": "trace-1" }""")
            }
        };

        AurikoProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(438, usage.GetProperty("prompt_tokens").GetInt32());
        Assert.Equal(0.00004452m, usage.GetProperty("estimated_cost").GetDecimal());
        Assert.Equal(0.00004452m, usage.GetProperty("cost").GetDecimal());

        var metadata = response.AdditionalProperties!["metadata"];
        Assert.Equal("trace-1", metadata.GetProperty("trace_id").GetString());
        Assert.Equal(0.00004452m, metadata.GetProperty("gateway").GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_writes_gateway_cost()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "chatcmpl-auriko-2",
            Created = 1785593130,
            Model = "phi-4",
            Usage = UsageElement("""
            {
                "prompt_tokens": 438,
                "completion_tokens": 99,
                "total_tokens": 537,
                "estimated_cost": 0.00004452
            }
            """)
        };

        AurikoProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var usage = Assert.IsType<JsonElement>(update.Usage);
        Assert.Equal(0.00004452m, usage.GetProperty("cost").GetDecimal());
        var gateway = update.AdditionalProperties!["metadata"].GetProperty("gateway");
        Assert.Equal(0.00004452m, gateway.GetProperty("cost").GetDecimal());
    }

    private static JsonElement UsageElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
