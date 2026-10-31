using System.Text.Json;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Tests.Vercel;

public sealed class RequestExtensionsTests
{
    [Fact]
    public void Provider_metadata_helpers_remain_provider_scoped_for_each_request_type()
    {
        var options = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            """{"test":{"value":3},"other":{"value":9}}""")!;

        Assert.Equal(3, new TranscriptionRequest { ProviderOptions = options }
            .GetProviderMetadata<Dictionary<string, int>>("test")!["value"]);
        Assert.Equal(3, new SpeechRequest { ProviderOptions = options }
            .GetProviderMetadata<Dictionary<string, int>>("test")!["value"]);
        Assert.Equal(3, new ImageRequest { ProviderOptions = options }
            .GetProviderMetadata<Dictionary<string, int>>("test")!["value"]);
        Assert.Equal(3, new VideoRequest { ProviderOptions = options }
            .GetProviderMetadata<Dictionary<string, int>>("test")!["value"]);
        Assert.Equal(3, new RerankingRequest { ProviderOptions = options }
            .GetProviderMetadata<Dictionary<string, int>>("test")!["value"]);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"other\":{\"value\":9}}")]
    [InlineData("{\"test\":null}")]
    public void Missing_or_null_provider_metadata_returns_default(string json)
    {
        var request = new ImageRequest
        {
            ProviderOptions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)
        };

        Assert.Null(request.GetProviderMetadata<Dictionary<string, int>>("test"));
    }

    [Fact]
    public void Try_deserialize_preserves_typed_values_and_accepts_json_and_objects()
    {
        object typed = new Dictionary<string, int> { ["value"] = 3 };
        object json = JsonSerializer.SerializeToElement(new { value = 3 });
        object value = new { value = 3 };

        Assert.Same(typed, typed.TryDeserialize<Dictionary<string, int>>());
        Assert.Equal(3, json.TryDeserialize<Dictionary<string, int>>()!["value"]);
        Assert.Equal(3, value.TryDeserialize<Dictionary<string, int>>()!["value"]);
    }

    [Fact]
    public void Try_deserialize_returns_default_for_null_or_incompatible_values()
    {
        object? missing = null;
        object invalid = "not an object";
        object json = JsonSerializer.SerializeToElement(42);

        Assert.Null(missing.TryDeserialize<Dictionary<string, int>>());
        Assert.Null(invalid.TryDeserialize<Dictionary<string, int>>());
        Assert.Null(json.TryDeserialize<Dictionary<string, int>>());
    }
}
