using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Tests.Contracts;

public sealed class ProviderMetadataRawPassthroughContractTests
{
   
    [Fact]
    public void Chat_request_provider_metadata_remains_provider_keyed_raw_json()
    {
        const string json = """
            {
              "id": "request-id",
              "model": "groq/model",
              "messages": [],
              "providerMetadata": {
                "groq": {"new_option": {"values": [1, null, true]}},
                "together": {"new_option": "unchanged"}
              }
            }
            """;

        var request = JsonSerializer.Deserialize<ChatRequest2>(json, JsonSerializerOptions.Web);
        var serialized = JsonSerializer.SerializeToElement(request, JsonSerializerOptions.Web);
        var providerMetadata = serialized.GetProperty("providerMetadata");
        using var expected = JsonDocument.Parse(json);

        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("providerMetadata").GetProperty("groq"),
            providerMetadata.GetProperty("groq")));
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("providerMetadata").GetProperty("together"),
            providerMetadata.GetProperty("together")));
        Assert.False(providerMetadata.TryGetProperty("options", out _));
    }
}
