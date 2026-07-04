using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.Providers.Requesty;
using AIHappey.Responses;

namespace AIHappey.Tests.Requesty;

public class RequestyProviderCostingTests
{
    [Fact]
    public void Response_enrichment_uses_numeric_requesty_usage_cost()
    {
        var response = new ResponseResult
        {
            Id = "rqsty-cmpl-1ce82bdf-20b9-4a57-92b4-6f3155a768d8",
            CreatedAt = 1783184646,
            Model = "glm-5",
            Usage = UsageElement("""
            {
                "input_tokens": 508,
                "input_tokens_details": { "cached_tokens": 256 },
                "output_tokens": 68,
                "output_tokens_details": { "reasoning_tokens": 27 },
                "total_tokens": 576,
                "cost": 0.0005208
            }
            """)
        };

        RequestyProvider.EnrichResponseWithGatewayCostForTests(response);

        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.0005208m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public void Response_enrichment_uses_string_requesty_usage_cost_and_preserves_existing_metadata()
    {
        var response = new ResponseResult
        {
            Id = "rqsty-cmpl-2",
            CreatedAt = 1783184646,
            Model = "gpt-5.1-2025-11-13",
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

        RequestyProvider.EnrichResponseWithGatewayCostForTests(response);

        Assert.Equal("trace-1", response.Metadata?["trace_id"]);
        var gateway = Assert.IsType<Dictionary<string, object?>>(response.Metadata?["gateway"]);
        Assert.Equal(0.000000123m, Assert.IsType<decimal>(gateway["cost"]));
    }

    [Fact]
    public void ChatCompletion_enrichment_writes_requesty_usage_cost_to_metadata_extension()
    {
        var response = new ChatCompletion
        {
            Id = "rqsty-cmpl-7f495aa6-1820-48d4-9943-af6ebf290290",
            Created = 1783184470,
            Model = "gpt-5.1-2025-11-13",
            Usage = UsageElement("""
            {
                "completion_tokens": 19,
                "completion_tokens_details": { "reasoning_tokens": 0 },
                "prompt_tokens": 327,
                "prompt_tokens_details": { "cached_tokens": 0 },
                "total_tokens": 346,
                "cost": 0.00059875
            }
            """)
        };

        RequestyProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        var gateway = response.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00059875m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletionUpdate_enrichment_writes_requesty_usage_cost_to_metadata_extension()
    {
        var update = new ChatCompletionUpdate
        {
            Id = "rqsty-cmpl-7f495aa6-1820-48d4-9943-af6ebf290290",
            Created = 1783184470,
            Model = "gpt-5.1-2025-11-13",
            Usage = UsageElement("""
            {
                "completion_tokens": 19,
                "prompt_tokens": 327,
                "total_tokens": 346,
                "cost": "0.00059875"
            }
            """)
        };

        RequestyProvider.EnrichChatCompletionUpdateWithGatewayCostForTests(update);

        var gateway = update.AdditionalProperties?["metadata"].GetProperty("gateway");
        Assert.Equal(0.00059875m, gateway?.GetProperty("cost").GetDecimal());
    }

    [Fact]
    public void ChatCompletion_enrichment_without_requesty_usage_cost_is_noop()
    {
        var response = new ChatCompletion
        {
            Id = "rqsty-cmpl-no-cost",
            Created = 1783184470,
            Model = "gpt-5.1-2025-11-13",
            Usage = UsageElement("""
            {
                "prompt_tokens": 327,
                "completion_tokens": 19,
                "total_tokens": 346
            }
            """)
        };

        RequestyProvider.EnrichChatCompletionWithGatewayCostForTests(response);

        Assert.Null(response.AdditionalProperties);
    }

    private static JsonElement UsageElement(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}

