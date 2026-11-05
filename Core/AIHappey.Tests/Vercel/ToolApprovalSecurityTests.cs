using System.Text.Json;
using System.Text.Json.Nodes;
using AIHappey.Core.Contracts;
using AIHappey.Core.Http;
using AIHappey.Core.Models;
using AIHappey.Unified.Mapping.Vercel;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Vercel;

public class ToolApprovalSecurityTests
{
    private static ToolApprovalSecurity Security(string? user = "user-a", string? secret = "test-only-secret-not-for-production")
        => new(Options.Create(new EndUserIdHashingOptions { SecretSalt = secret }), new Identity(user));

    private static ToolCallPart Call(object? input = null) => new()
    {
        ToolCallId = "call-1", ToolName = "search", Title = "Search", ProviderExecuted = false,
        Input = input ?? JsonSerializer.Deserialize<JsonElement>("{\"query\":\"example\",\"limit\":1}")
    };

    private static ChatRequest Request(string? signature, string state = "approval-responded", bool approved = true)
        => new()
        {
            Model = "test/model", Id = "conversation",
            Messages = [new UIMessage { Id = "assistant", Role = Role.assistant, Parts = [new ToolInvocationPart
            {
                Type = "tool-search", Title = "Search", ToolCallId = "call-1", Input = Call().Input,
                State = state, ProviderExecuted = false, Approval = new ToolApproval
                { Id = "approval-1", Approved = approved, Signature = signature, IsAutomatic = true, Reason = "search" }
            }] }]
        };

    [Theory]
    [InlineData("approval-responded", true)]
    [InlineData("approval-responded", false)]
    [InlineData("output-available", true)]
    [InlineData("output-error", false)]
    [InlineData("output-denied", false)]
    public void SignedDecisionsAndPersistedHistoryRemainValid(string state, bool approved)
    {
        var security = Security();
        var signature = security.CreateSignature("user-a", "approval-1", Call());
        var request = Request(signature, state, approved);
        var restored = JsonSerializer.Deserialize<ChatRequest>(JsonSerializer.Serialize(request, JsonSerializerOptions.Web), JsonSerializerOptions.Web)!;
        security.ValidateIncoming(restored);
        security.ValidateIncoming(restored); // No expiry or single-use ledger.
        Assert.True(((ToolInvocationPart)restored.Messages[0].Parts[0]).Approval!.IsAutomatic);
        Assert.Equal(signature, ((ToolInvocationPart)restored.Messages[0].Parts[0]).Approval!.Signature);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unsigned")]
    [InlineData("v2.0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("v1.zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("v1.0000000000000000000000000000000000000000000000000000000000000000")]
    public void MissingMalformedOrForgedSignaturesAreRejected(string? signature)
        => Assert.Throws<ToolApprovalValidationException>(() => Security().ValidateIncoming(Request(signature)));

    [Theory]
    [InlineData("toolCallId", "another-call")]
    [InlineData("type", "tool-delete")]
    [InlineData("title", "Delete")]
    [InlineData("providerExecuted", true)]
    public void SignedToolContextCannotBeChanged(string property, object value)
    {
        var security = Security();
        var node = JsonSerializer.SerializeToNode(Request(security.CreateSignature("user-a", "approval-1", Call())), JsonSerializerOptions.Web)!;
        node["messages"]![0]!["parts"]![0]![property] = JsonValue.Create(value);
        Assert.Throws<ToolApprovalValidationException>(() => security.ValidateIncoming(node.Deserialize<ChatRequest>(JsonSerializerOptions.Web)!));
    }

    [Fact]
    public void ApprovalIdentityArgumentsUserAndKeyAreBound()
    {
        var security = Security();
        var request = Request(security.CreateSignature("user-a", "approval-1", Call()));
        Assert.Throws<ToolApprovalValidationException>(() => Security("user-b").ValidateIncoming(request));
        Assert.Throws<ToolApprovalValidationException>(() => Security(secret: "different-secret").ValidateIncoming(request));
        foreach (var property in new[] { "input", "approval" })
        {
            var node = JsonSerializer.SerializeToNode(request, JsonSerializerOptions.Web)!;
            var part = node["messages"]![0]!["parts"]![0]!;
            if (property == "input") part["input"]!["query"] = "changed";
            else part["approval"]!["id"] = "changed";
            Assert.Throws<ToolApprovalValidationException>(() => security.ValidateIncoming(node.Deserialize<ChatRequest>(JsonSerializerOptions.Web)!));
        }
    }

    [Fact]
    public void CanonicalizationIgnoresObjectOrderAndNumberNotationButNotArrayOrderOrPrecision()
    {
        string Sign(string json) => Security().CreateSignature("user-a", "approval-1", Call(JsonSerializer.Deserialize<JsonElement>(json)));
        Assert.Equal(Sign("{\"b\":[1.0,-0,1e-7],\"a\":\"é\"}"), Sign("{\"a\":\"\\u00e9\",\"b\":[1,0,0.0000001]}"));
        Assert.NotEqual(Sign("[1,2]"), Sign("[2,1]"));
        Assert.NotEqual(Sign("9007199254740992"), Sign("9007199254740993"));
        Assert.NotEqual(Sign("0.123456789012345678"), Sign("0.123456789012345679"));
        Assert.Throws<JsonException>(() => Sign("{\"a\":1,\"a\":2}"));
    }

    [Fact]
    public void SigningFailsClosedWithoutSecretOrIdentity()
    {
        Assert.Throws<InvalidOperationException>(() => Security(secret: " ").CreateSignature("user-a", "approval-1", Call()));
        Assert.Throws<InvalidOperationException>(() => Security().CreateSignature(null, "approval-1", Call()));
        Assert.Throws<ToolApprovalValidationException>(() => Security(user: null).ValidateIncoming(Request("v1." + new string('0', 64))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignsBothApprovalRequestVariantsAndHandlesEarlyRequests(bool legacy)
    {
        UIMessagePart approval = legacy
            ? new ToolApprovalRequestPart { ApprovalId = "approval-1", ToolCallId = "call-1" }
            : new ToolApprovalRequestUIPart { ApprovalId = "approval-1", ToolCallId = "call-1", Reason = "Review", ApprovalDescriptor = new { label = "Search" } };
        var result = new List<UIMessagePart>();
        await foreach (var part in Security().SignOutgoing(new ChatRequest(), Events(approval, Call(), new FinishUIPart()))) result.Add(part);
        Assert.IsType<ToolCallPart>(result[0]);
        var raw = JsonSerializer.SerializeToElement(result[1], JsonSerializerOptions.Web);
        Assert.Equal(Security().CreateSignature("user-a", "approval-1", Call()), raw.GetProperty("signature").GetString());
        if (!legacy) Assert.Equal("Review", raw.GetProperty("reason").GetString());
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { await foreach (var _ in Security().SignOutgoing(new ChatRequest(), Events(approval, new FinishUIPart()))) { } });
    }

    [Fact]
    public async Task HeaderGatewayRejectsUnsignedApprovalBeforeResolvingProviderOrStartingStream()
    {
        var resolver = new RejectProvider();
        var controller = new AIHappey.HeaderAuth.Controllers.ChatController(resolver, Security())
        { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        Assert.IsType<BadRequestObjectResult>(await controller.Post(Request(null), default));
        Assert.False(resolver.Called);
        Assert.False(controller.Response.HasStarted);
    }

    [Fact]
    public void UnifiedStreamApprovalMappingPreservesAllApprovalMetadata()
    {
        var envelope = new AIEventEnvelope { Type = "tool-approval-request", Data = new AIToolApprovalRequestEventData
        {
            ApprovalId = "approval-1", ToolCallId = "call-1", Signature = "signature", IsAutomatic = true,
            Reason = "Review", ApprovalDescriptor = new { label = "Search" }, InputSchemaInput = new { query = "example" }
        } };
        var part = Assert.IsType<ToolApprovalRequestUIPart>(Assert.Single(envelope.ToUIMessagePart("test")));
        Assert.Equal("signature", part.Signature); Assert.True(part.IsAutomatic);
        Assert.Equal("Review", part.Reason); Assert.NotNull(part.ApprovalDescriptor); Assert.NotNull(part.InputSchemaInput);
    }

    private static async IAsyncEnumerable<UIMessagePart> Events(params UIMessagePart[] parts)
    { foreach (var part in parts) yield return part; await Task.CompletedTask; }

    private sealed class Identity(string? user) : IEndUserIdResolver
    { public string? Resolve(ChatRequest request) => user; }

    private sealed class RejectProvider : IAIModelProviderResolver
    {
        public bool Called { get; private set; }
        public Task<IModelProvider> Resolve(string model, CancellationToken ct = default)
        { Called = true; throw new InvalidOperationException("Provider must not be resolved."); }
        public IModelProvider GetProvider() => throw new NotSupportedException();
        public Task<ModelResponse> ResolveModels(CancellationToken ct) => throw new NotSupportedException();
    }
}
