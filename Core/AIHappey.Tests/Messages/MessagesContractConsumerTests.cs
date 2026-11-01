using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Tests.TestInfrastructure;
using AIHappey.Unified.Models;

namespace AIHappey.Tests.Messages;

public sealed class MessagesContractConsumerTests
{
    private const string ContextJson = """
        {"edits":[{"type":"compact_20260112","trigger":{"type":"input_tokens","value":100000},
          "keep":{"type":"tool_uses","value":3},"clear_at_least":{"type":"input_tokens","value":1000},
          "clear_tool_inputs":true,"exclude_tools":["lookup"],"instructions":"Keep decisions",
          "pause_after_compaction":true,"future_edit":{"values":[true,null,3]}}],
          "future_context":{"enabled":false}}
        """;
    private const string CompactionJson = """
        {"type":"on_demand","instructions":"Keep decisions","future_compaction":[1,null,{"flag":false}]}
        """;

    [Theory]
    [InlineData("provider-json")]
    [InlineData("provider-typed")]
    [InlineData("round-trip")]
    public void Typed_configurations_preserve_native_fields_and_unknown_JSON(string source)
    {
        var native = new MessagesRequest
        {
            Model = "model", MaxTokens = 32, Messages = [],
            ContextManagement = Deserialize<MessagesContextManagementConfig>(ContextJson),
            Compaction = Deserialize<MessagesCompactionConfig>(CompactionJson)
        };
        var request = source == "round-trip"
            ? native.ToUnifiedRequest("provider")
            : new AIRequest
            {
                ProviderId = "provider",
                Model = "model", MaxOutputTokens = 32,
                Metadata = new()
                {
                    ["provider"] = new Dictionary<string, object?>
                    {
                        ["context_management"] = source == "provider-typed" ? native.ContextManagement : Element(ContextJson),
                        ["compaction"] = source == "provider-typed" ? native.Compaction : Element(CompactionJson)
                    }
                }
            };

        var mapped = request.ToMessagesRequest("provider");
        var context = Assert.IsType<MessagesContextManagementConfig>(mapped.ContextManagement);
        var compaction = Assert.IsType<MessagesCompactionConfig>(mapped.Compaction);
        Assert.Equal("compact_20260112", Assert.Single(Assert.IsType<List<MessagesContextManagementEdit>>(context.Edits)).Type);
        Assert.Equal("on_demand", compaction.Type);
        var json = JsonSerializer.SerializeToElement(mapped, MessagesJson.Default);
        Assert.True(JsonElement.DeepEquals(Element(ContextJson), json.GetProperty("context_management")));
        Assert.True(JsonElement.DeepEquals(Element(CompactionJson), json.GetProperty("compaction")));
        Assert.False(mapped.AdditionalProperties?.ContainsKey("context_management") == true);
        Assert.False(mapped.AdditionalProperties?.ContainsKey("compaction") == true);
    }

    [Fact]
    public void Provider_configurations_take_precedence_over_round_trip_metadata()
    {
        var request = new AIRequest
        {
            ProviderId = "provider",
            Model = "model", MaxOutputTokens = 32,
            Metadata = new()
            {
                ["provider"] = new { context_management = Element(ContextJson), compaction = Element(CompactionJson) },
                ["messages.request.context_management"] = new MessagesContextManagementConfig { Edits = [] },
                ["messages.request.compaction"] = new MessagesCompactionConfig { Type = "other" }
            }
        };

        var mapped = request.ToMessagesRequest("provider");

        Assert.True(JsonElement.DeepEquals(Element(ContextJson), JsonSerializer.SerializeToElement(mapped.ContextManagement, MessagesJson.Default)));
        Assert.True(JsonElement.DeepEquals(Element(CompactionJson), JsonSerializer.SerializeToElement(mapped.Compaction, MessagesJson.Default)));
    }

    [Fact]
    public void Absent_configurations_remain_absent()
    {
        var mapped = new AIRequest { ProviderId = "provider", Model = "model", MaxOutputTokens = 32 }.ToMessagesRequest("provider");
        var json = JsonSerializer.SerializeToElement(mapped, MessagesJson.Default);

        Assert.Null(mapped.ContextManagement);
        Assert.Null(mapped.Compaction);
        Assert.False(json.TryGetProperty("context_management", out _));
        Assert.False(json.TryGetProperty("compaction", out _));
    }

    [Theory]
    [InlineData(32, 64, 32)]
    [InlineData(null, 64, 64)]
    [InlineData(32, null, 32)]
    [InlineData(0, 64, 0)]
    [InlineData(-1, 64, -1)]
    [InlineData(null, 0, 0)]
    public void Token_limits_preserve_explicit_precedence_without_replacing_invalid_values(int? requested, int? providerOption, int expected)
    {
        var mapped = new AIRequest
        {
            ProviderId = "provider",
            Model = "model", MaxOutputTokens = requested,
            Metadata = new() { ["provider"] = new { max_tokens = providerOption } }
        }.ToMessagesRequest("provider");

        Assert.Equal(expected, mapped.MaxTokens);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"max_tokens\":null}")]
    [InlineData("{\"max_tokens\":\"invalid\"}")]
    public void Missing_usable_token_limit_is_explicit_instead_of_inventing_a_default(string options)
    {
        var request = new AIRequest { ProviderId = "provider", Model = "model", Metadata = new() { ["provider"] = Element(options) } };

        var error = Assert.Throws<ArgumentException>(() => request.ToMessagesRequest("provider"));

        Assert.Equal("request", error.ParamName);
        Assert.Contains("Messages requires max_tokens", error.Message);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(32, true)]
    [InlineData(0, false)]
    [InlineData(-1, false)]
    public void Provider_defaults_use_typed_context_and_never_override_required_token_limits(int maxTokens, bool explicitContext)
    {
        var context = new MessagesContextManagementConfig { Edits = [] };
        var request = new MessagesRequest
        {
            Model = "model", MaxTokens = maxTokens, Messages = [],
            ContextManagement = explicitContext ? context : null,
            Metadata = new()
            {
                UserId = "user",
                AdditionalProperties = new()
                {
                    ["provider"] = JsonSerializer.SerializeToElement(new { context_management = Element(ContextJson), max_tokens = 64 })
                }
            }
        };

        new FixtureMessageStreamModelProvider("provider", []).SetDefaultMessagesProperties(request);

        Assert.Equal(maxTokens, request.MaxTokens);
        Assert.Equal("user", request.Metadata.UserId);
        Assert.Null(request.Metadata.AdditionalProperties);
        if (explicitContext)
            Assert.Same(context, request.ContextManagement);
        else
            Assert.True(JsonElement.DeepEquals(Element(ContextJson), JsonSerializer.SerializeToElement(request.ContextManagement, MessagesJson.Default)));
    }

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, MessagesJson.Default) ?? throw new InvalidOperationException("Invalid test fixture.");

    private static JsonElement Element(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
