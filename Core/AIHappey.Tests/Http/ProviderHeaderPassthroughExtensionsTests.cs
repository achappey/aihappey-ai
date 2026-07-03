using AIHappey.Core.Extensions;

namespace AIHappey.Tests.Http;

public class ProviderHeaderPassthroughExtensionsTests
{
    [Theory]
    [InlineData("anthropic-beta")]
    [InlineData("Anthropic-Beta")]
    [InlineData("x-anthropic-beta")]
    [InlineData("X-AnThRoPiC-Beta")]
    public void IsProviderPassthroughHeader_allows_provider_prefixed_headers_case_insensitively(string headerName)
    {
        Assert.True(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(headerName, "anthropic"));
    }

    [Theory]
    [InlineData("x-anthropic-key")]
    [InlineData("X-AnThRoPiC-Key")]
    public void IsProviderPassthroughHeader_excludes_x_provider_key_case_insensitively(string headerName)
    {
        Assert.False(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(headerName, "anthropic"));
    }

    [Theory]
    [InlineData("anthropic-key")]
    [InlineData("x-anthropic-api-key")]
    public void IsProviderPassthroughHeader_keeps_other_matching_key_like_headers(string headerName)
    {
        Assert.True(ProviderHeaderPassthroughExtensions.IsProviderPassthroughHeader(headerName, "anthropic"));
    }

    [Fact]
    public void GetProviderPassthroughHeaders_preserves_original_header_names_and_values()
    {
        var headers = new List<KeyValuePair<string, string?>>
        {
            new("Anthropic-Beta", "computer-use-2025-01-24"),
            new("X-AnThRoPiC-Feature", "exact raw value"),
            new("X-AnThRoPiC-Key", "blocked-secret"),
            new("openai-beta", "wrong-provider")
        };

        var passthrough = headers.GetProviderPassthroughHeaders("anthropic");

        Assert.Equal(2, passthrough.Count);
        Assert.Contains(passthrough, h => h.Key == "Anthropic-Beta" && h.Value == "computer-use-2025-01-24");
        Assert.Contains(passthrough, h => h.Key == "X-AnThRoPiC-Feature" && h.Value == "exact raw value");
        Assert.DoesNotContain(passthrough, h => h.Key == "X-AnThRoPiC-Key");
        Assert.DoesNotContain(passthrough, h => h.Key == "openai-beta");
    }
}
