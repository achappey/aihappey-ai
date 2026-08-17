using System.Text.Json;
using AIHappey.Core.Providers.SpaceXAI;
using AIHappey.Responses;

namespace AIHappey.Tests.SpaceXAI;

public sealed class SpaceXAIResponsesCostingTests
{
    [Fact]
    public void Response_cost_enrichment_reads_usd_ticks_from_typed_usage_raw_data()
    {
        var response = CreateResponseWithUsage("""
            {
              "input_tokens": 100924,
              "output_tokens": 4649,
              "total_tokens": 105573,
              "cost_in_usd_ticks": 202574000,
              "server_side_tool_usage_details": { "web_search_calls": 10 }
            }
            """);

        SpaceXAIProvider.EnrichResponseWithGatewayCostForTests(response);

        Assert.Equal(0.0202574m, GetGatewayCost(response));
    }

    [Fact]
    public void Completed_stream_response_uses_the_same_typed_usage_cost_enrichment()
    {
        var completedResponse = CreateResponseWithUsage("""
            {
              "input_tokens": 12,
              "output_tokens": 3,
              "total_tokens": 15,
              "cost_in_usd_ticks": "250000000"
            }
            """);

        SpaceXAIProvider.EnrichResponseWithGatewayCostForTests(completedResponse);

        Assert.Equal(0.025m, GetGatewayCost(completedResponse));
    }

    private static ResponseResult CreateResponseWithUsage(string usageJson)
        => new()
        {
            Id = "response-test",
            Model = "grok-test",
            Usage = JsonSerializer.Deserialize<ResponseUsage>(usageJson, JsonSerializerOptions.Web)
        };

    private static decimal GetGatewayCost(ResponseResult response)
    {
        Assert.NotNull(response.Metadata);
        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata["gateway"]);
        return Assert.IsType<decimal>(gateway["cost"]);
    }
}
