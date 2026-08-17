using System.Text.Json;
using AIHappey.Responses;

namespace AIHappey.Tests.Responses;

public sealed class ResponseUsageSerializationTests
{
    [Fact]
    public void RoundTrip_preserves_canonical_usage_and_provider_extensions()
    {
        const string json = """
            {
              "input_tokens": 100924,
              "output_tokens": 4649,
              "total_tokens": 105573,
              "cost_in_usd_ticks": 202574000,
              "num_server_side_tools_used": 12,
              "server_side_tool_usage_details": {
                "web_search_calls": 10,
                "x_search_calls": 0
              }
            }
            """;

        var usage = JsonSerializer.Deserialize<ResponseUsage>(json, JsonSerializerOptions.Web);

        Assert.NotNull(usage);
        Assert.Equal(100924, usage.InputTokens);
        Assert.Equal(4649, usage.OutputTokens);
        Assert.Equal(105573, usage.TotalTokens);
        Assert.Equal(202574000, usage.AdditionalProperties!["cost_in_usd_ticks"].GetInt32());

        var serialized = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);

        Assert.Equal(100924, serialized.GetProperty("input_tokens").GetInt32());
        Assert.Equal(202574000, serialized.GetProperty("cost_in_usd_ticks").GetInt32());
        Assert.Equal(12, serialized.GetProperty("num_server_side_tools_used").GetInt32());
        Assert.Equal(
            10,
            serialized.GetProperty("server_side_tool_usage_details")
                .GetProperty("web_search_calls")
                .GetInt32());
    }

    [Fact]
    public void Serialization_does_not_allow_extension_data_to_duplicate_canonical_fields()
    {
        var usage = new ResponseUsage
        {
            InputTokens = 42,
            AdditionalProperties = new Dictionary<string, JsonElement>
            {
                ["INPUT_TOKENS"] = JsonSerializer.SerializeToElement(999),
                ["provider_value"] = JsonSerializer.SerializeToElement("retained")
            }
        };

        var serialized = JsonSerializer.SerializeToElement(usage, JsonSerializerOptions.Web);

        Assert.Equal(42, serialized.GetProperty("input_tokens").GetInt32());
        Assert.False(serialized.TryGetProperty("INPUT_TOKENS", out _));
        Assert.Equal("retained", serialized.GetProperty("provider_value").GetString());
    }
}
