using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.DeepInfra;

namespace AIHappey.Tests.DeepInfra;

public class DeepInfraProviderCostingTests
{
    [Fact]
    public void GetGatewayCost_uses_numeric_estimated_cost()
    {
        var usage = UsageElement("""
        {
            "prompt_tokens": 373,
            "total_tokens": 414,
            "completion_tokens": 41,
            "estimated_cost": 0.00046988999999999993
        }
        """);

        var cost = DeepInfraProvider.GetGatewayCost(usage);

        Assert.Equal(0.00046988999999999993m, cost);
    }

    [Fact]
    public void GetGatewayCost_uses_string_estimated_cost()
    {
        var usage = UsageElement("""
        {
            "prompt_tokens": 10,
            "completion_tokens": 2,
            "total_tokens": 12,
            "estimated_cost": "1.23e-7"
        }
        """);

        var cost = DeepInfraProvider.GetGatewayCost(usage);

        Assert.Equal(0.000000123m, cost);
    }

    [Fact]
    public void ChatCompletion_enrichment_writes_estimated_cost_to_metadata_gateway()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-deepinfra-1",
            Created = 1783023397,
            Model = "zai-org/GLM-5.2",
            Usage = UsageElement("""
            {
                "prompt_tokens": 373,
                "total_tokens": 414,
                "completion_tokens": 41,
                "estimated_cost": 0.00046988999999999993
            }
            """)
        };

        DeepInfraProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var usage = Assert.IsType<JsonElement>(response.Usage);
        Assert.Equal(0.00046988999999999993m, usage.GetProperty("cost").GetDecimal());
        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00046988999999999993m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_writes_estimated_cost_to_metadata_gateway()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "chatcmpl-deepinfra-2",
            Created = 1783023397,
            Model = "zai-org/GLM-5.2",
            Usage = UsageElement("""
            {
                "prompt_tokens": 373,
                "total_tokens": 414,
                "completion_tokens": 41,
                "estimated_cost": 0.00046988999999999993
            }
            """)
        };

        DeepInfraProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var usage = Assert.IsType<JsonElement>(update.Usage);
        Assert.Equal(0.00046988999999999993m, usage.GetProperty("cost").GetDecimal());
        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00046988999999999993m, gateway?.GetProperty("cost").GetDecimal());
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
