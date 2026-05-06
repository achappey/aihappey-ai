using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Responses;

namespace AIHappey.Tests.OpenRouter;

public class OpenRouterProviderCostingTests
{
    [Fact]
    public void Response_enrichment_uses_numeric_openrouter_usage_cost()
    {
        var response = new ResponseResult
        {
            Id = "gen-1778046614-XtANNOGTY9X8CPUnR9QB",
            CreatedAt = 1778046614,
            Model = "ibm-granite/granite-4.1-8b-20260429",
            Usage = UsageElement("""
            {
                "input_tokens": 9292,
                "output_tokens": 1319,
                "total_tokens": 10611,
                "cost": 0.0005964999999999999
            }
            """)
        };

        OpenRouterProvider.EnrichResponseWithGatewayCostForTests(response);

        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.0005964999999999999m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public void Response_enrichment_uses_string_openrouter_usage_cost_and_preserves_existing_metadata()
    {
        var response = new ResponseResult
        {
            Id = "gen-2",
            CreatedAt = 1778046614,
            Model = "openai/gpt-4o-mini",
            Metadata = new Dictionary<string, object?>
            {
                ["trace_id"] = "trace-1"
            },
            Usage = UsageElement("""
            {
                "input_tokens": 10,
                "output_tokens": 5,
                "total_tokens": 15,
                "cost": "1.23e-7"
            }
            """)
        };

        OpenRouterProvider.EnrichResponseWithGatewayCostForTests(response);

        Assert.Equal("trace-1", response.Metadata?["trace_id"]);
        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.000000123m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public void ChatCompletion_enrichment_writes_openrouter_usage_cost_to_metadata_extension()
    {
        var response = new ChatCompletion
        {
            Id = "chatcmpl-1",
            Created = 1778046614,
            Model = "anthropic/claude-sonnet-4.5",
            Usage = UsageElement("""
            {
                "prompt_tokens": 100,
                "completion_tokens": 20,
                "total_tokens": 120,
                "cost": 0.00042
            }
            """)
        };

        OpenRouterProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00042m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_writes_openrouter_usage_cost_to_metadata_extension()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "chatcmpl-2",
            Created = 1778046614,
            Model = "google/gemini-2.5-flash",
            Usage = UsageElement("""
            {
                "prompt_tokens": 50,
                "completion_tokens": 10,
                "total_tokens": 60,
                "cost": "0.000021"
            }
            """)
        };

        OpenRouterProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.000021m, gateway?.GetProperty("cost").GetDecimal());
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
