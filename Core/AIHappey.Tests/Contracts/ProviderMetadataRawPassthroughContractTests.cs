using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers;

namespace AIHappey.Tests.Contracts;

public sealed class ProviderMetadataRawPassthroughContractTests
{
    [Fact]
    public void Provider_metadata_preserves_groq_and_together_unknown_json_without_extra_nesting()
    {
        const string json = """
            {
              "groq": {
                "reasoning": {"effort": "high", "future_reasoning": {"enabled": true}},
                "parallel_tool_calls": false,
                "future_config": {"items": [1, null, {"value": "kept"}]},
                "future_array": [true, "text", 2.5],
                "future_primitive": 123,
                "future_null": null
              },
              "together": {
                "reasoning_effort": "medium",
                "future_config": {"nested": ["kept", null]},
                "future_primitive": false
              }
            }
            """;

        var metadata = JsonSerializer.Deserialize<ProviderMetadata>(json, JsonSerializerOptions.Web);

        Assert.NotNull(metadata);
        Assert.True(metadata.Groq.HasValue);
        Assert.True(metadata.Together.HasValue);
        var groq = metadata.Groq.Value;
        var together = metadata.Together.Value;
        Assert.Equal(JsonValueKind.Object, groq.ValueKind);
        Assert.Equal(JsonValueKind.Object, together.ValueKind);
        Assert.Equal("high", groq.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(3, groq.GetProperty("future_config").GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null,
            groq.GetProperty("future_config").GetProperty("items")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, groq.GetProperty("future_null").ValueKind);

        var serialized = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
        using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("groq"),
            serialized.GetProperty("groq")));
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("together"),
            serialized.GetProperty("together")));
        Assert.False(serialized.GetProperty("groq").TryGetProperty("providerOptions", out _));
        Assert.False(serialized.GetProperty("together").TryGetProperty("providerMetadata", out _));
    }

    [Fact]
    public void Provider_metadata_preserves_perplexity_and_jina_known_and_unknown_json()
    {
        const string json = """
            {
              "perplexity": {
                "reasoning_effort": "high",
                "tools": [{"type":"search","future":{"enabled":true}}],
                "max_steps": 4,
                "future_config": {"items": [1, null, {"value": "kept"}]},
                "future_array": [true, "text", 2.5],
                "future_null": null
              },
              "jina": {
                "reasoning_effort": "medium",
                "no_direct_answer": true,
                "bad_hostnames": ["example.com"],
                "future_config": {"nested": ["kept", null]},
                "future_primitive": 123
              }
            }
            """;

        var metadata = JsonSerializer.Deserialize<ProviderMetadata>(json, JsonSerializerOptions.Web);

        Assert.NotNull(metadata);
        Assert.True(metadata.Perplexity.HasValue);
        Assert.True(metadata.Jina.HasValue);
        var perplexity = metadata.Perplexity.Value;
        var jina = metadata.Jina.Value;
        Assert.Equal("high", perplexity.GetProperty("reasoning_effort").GetString());
        Assert.Equal(4, perplexity.GetProperty("max_steps").GetInt32());
        Assert.Equal("search", perplexity.GetProperty("tools")[0].GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.Null,
            perplexity.GetProperty("future_config").GetProperty("items")[1].ValueKind);
        Assert.Equal(JsonValueKind.Null, perplexity.GetProperty("future_null").ValueKind);
        Assert.Equal("medium", jina.GetProperty("reasoning_effort").GetString());
        Assert.True(jina.GetProperty("no_direct_answer").GetBoolean());
        Assert.Equal("example.com", jina.GetProperty("bad_hostnames")[0].GetString());
        Assert.Equal(123, jina.GetProperty("future_primitive").GetInt32());

        var serialized = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
        using var expected = JsonDocument.Parse(json);
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("perplexity"),
            serialized.GetProperty("perplexity")));
        Assert.True(JsonElement.DeepEquals(
            expected.RootElement.GetProperty("jina"),
            serialized.GetProperty("jina")));
    }

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
