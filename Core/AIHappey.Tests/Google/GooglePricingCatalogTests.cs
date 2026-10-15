using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Tests.Google;

public sealed class GooglePricingCatalogTests
{
    [Fact]
    public void Standard_pricing_uses_rates_effective_on_requested_date()
    {
        var currentPricing = "google".GetPricing(DateTimeOffset.Parse("2026-10-15T00:00:00Z"))!;
        var futurePricing = "google".GetPricing(DateTimeOffset.Parse("2027-01-01T00:00:00Z"))!;

        Assert.Equal(0.000002m, currentPricing["google/gemini-4-argon"].Input);
        Assert.Equal(0.00001m, currentPricing["google/gemini-4-argon"].Output);
        Assert.Equal(0.000004m, futurePricing["google/gemini-4-argon"].Input);
        Assert.Equal(0.00002m, futurePricing["google/gemini-4-argon"].Output);
        Assert.Equal(0.00000075m, currentPricing["google/gemini-3.8-flash"].Input);
        Assert.Equal(0.0000015m, futurePricing["google/gemini-3.8-flash"].Input);
    }

    [Theory]
    [InlineData("batch", "0.000001", "0.000005")]
    [InlineData("flex", "0.000001", "0.000005")]
    [InlineData("priority", "0.0000036", "0.000018")]
    [InlineData("contributor", "0.000001", "0.000005")]
    public void Tier_catalogs_resolve_current_argon_rates(string tier, string input, string output)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Catalog",
            "Pricing",
            "providers",
            "google",
            $"{tier}.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var entry = document.RootElement.GetProperty("google/gemini-4-argon");
        var pricing = ModelPricingCatalog.Resolve(entry, DateTimeOffset.Parse("2026-10-15T00:00:00Z"))!;

        Assert.Equal(decimal.Parse(input), pricing.Input);
        Assert.Equal(decimal.Parse(output), pricing.Output);
    }

    [Fact]
    public void Newly_listed_google_models_have_expected_model_types()
    {
        Assert.Equal("audio", "gemini-3.8-live".GuessModelType());
        Assert.Equal("audio", "gemini-3.8-live-extended-thinking".GuessModelType());
        Assert.Equal("transcription", "gemini-3.5-transcribe-live".GuessModelType());
        Assert.Equal("transcription", "gemini-3.5-transcribe".GuessModelType());
        Assert.Equal("video", "gemini-omni-1.1-flash-video".GuessModelType());
    }
}
