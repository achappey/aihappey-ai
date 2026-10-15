using AIHappey.Telemetry.MCP;

namespace AIHappey.Tests.Telemetry;

public class TelemetryEstimatedCostTests
{
    [Fact]
    public void Calculate_UsesCatalogInputAndOutputPricing()
    {
        var cost = TelemetryEstimatedCost.Calculate(
            "openai",
            "openai/gpt-5.2",
            inputTokens: 1_000,
            outputTokens: 500);

        Assert.Equal(0.00875m, cost);
    }

    [Fact]
    public void Calculate_ReturnsNullWhenCatalogPricingIsUnavailable()
    {
        var cost = TelemetryEstimatedCost.Calculate(
            "unknown-provider",
            "unknown-model",
            inputTokens: 1_000,
            outputTokens: 500);

        Assert.Null(cost);
    }

    [Theory]
    [InlineData(null, 500)]
    [InlineData(1_000, null)]
    public void Calculate_ReturnsNullWhenUsageIsUnavailable(int? inputTokens, int? outputTokens)
    {
        var cost = TelemetryEstimatedCost.Calculate(
            "openai",
            "openai/gpt-5.2",
            inputTokens,
            outputTokens);

        Assert.Null(cost);
    }
}
