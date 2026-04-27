using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.EUrouter;
using AIHappey.Responses;

namespace AIHappey.Tests.EUrouter;

public class EUrouterProviderModelsTests
{
    [Theory]
    [InlineData("[\"chat.completions\",\"responses\",\"completions\"]", true)]
    [InlineData("[\"chat.completions\",\"completions\"]", false)]
    public void SupportsResponsesEndpoint_uses_supported_api_endpoints(string endpointsJson, bool expected)
    {
        using var doc = JsonDocument.Parse($$"""
        {
            "id": "sample-model",
            "supported_api_endpoints": {{endpointsJson}}
        }
        """);

        Assert.Equal(expected, EUrouterProvider.SupportsResponsesEndpoint(doc.RootElement.Clone()));
    }

    [Fact]
    public void SupportsResponsesEndpoint_defaults_to_false_when_metadata_is_missing()
    {
        using var doc = JsonDocument.Parse("{\"id\":\"sample-model\"}");

        Assert.False(EUrouterProvider.SupportsResponsesEndpoint(doc.RootElement.Clone()));
        Assert.False(EUrouterProvider.SupportsResponsesEndpoint(null));
    }

    [Fact]
    public void ChatCompletion_enrichment_uses_eurouter_usage_cost_when_present()
    {
        var response = new ChatCompletion
        {
            Id = "gen-1",
            Created = 1777285265,
            Model = "tensorix/gpt-oss-20b",
            Usage = UsageElement("""
            {
                "prompt_tokens": 444,
                "completion_tokens": 13,
                "total_tokens": 457,
                "cost": 0.00001514
            }
            """)
        };

        EUrouterProvider.EnrichChatCompletionWithGatewayCostForTests(response, pricing: null);

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00001514m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_calculates_cost_from_pricing_when_usage_cost_is_missing()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "gen-2",
            Created = 1777285265,
            Model = "tensorix/gpt-oss-20b",
            Usage = UsageElement("""
            {
                "prompt_tokens": 444,
                "completion_tokens": 13,
                "total_tokens": 457,
                "prompt_tokens_details": {
                    "cached_tokens": 10,
                    "cache_write_tokens": 5
                }
            }
            """)
        };

        EUrouterProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update, new ModelPricing
        {
            Input = 0.00000003m,
            Output = 0.00000014m,
            InputCacheRead = 0.00000001m,
            InputCacheWrite = 0.00000002m
        });

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00001534m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void Response_enrichment_uses_eurouter_usage_cost_when_present()
    {
        var response = new ResponseResult
        {
            Id = "resp-1",
            CreatedAt = 1777284227,
            Model = "microsoft-foundry/gpt-4o",
            Usage = UsageElement("""
            {
                "input_tokens": 438,
                "output_tokens": 12,
                "total_tokens": 450,
                "cost": "0.00012345"
            }
            """)
        };

        EUrouterProvider.EnrichResponseWithGatewayCostForTests(response, pricing: null);

        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.00012345m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public void Response_enrichment_calculates_cost_from_pricing_when_usage_cost_is_missing()
    {
        var response = new ResponseResult
        {
            Id = "resp-2",
            CreatedAt = 1777284227,
            Model = "microsoft-foundry/gpt-4o",
            Usage = UsageElement("""
            {
                "input_tokens": 438,
                "output_tokens": 12,
                "total_tokens": 450,
                "input_tokens_details": {
                    "cached_tokens": 8
                }
            }
            """)
        };

        EUrouterProvider.EnrichResponseWithGatewayCostForTests(response, new ModelPricing
        {
            Input = 0.00000002m,
            Output = 0.00000008m,
            InputCacheRead = 0.000000005m
        });

        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.00000976m, Assert.IsType<decimal>(gateway["cost"]));
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
