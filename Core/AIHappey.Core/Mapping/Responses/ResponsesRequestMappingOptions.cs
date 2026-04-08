using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public sealed class ResponsesRequestMappingOptions
{
    public string? Model { get; init; }

    public string? Instructions { get; init; }

    public string? InputImageDetail { get; init; }

    public bool? Stream { get; init; }

    public bool? Store { get; init; }

    public bool? ParallelToolCalls { get; init; }

    public string? ServiceTier { get; init; }

    public object? ToolChoice { get; init; }

    public object? Text { get; init; }
    public Reasoning? Reasoning { get; init; }

    public Dictionary<string, object?>? Metadata { get; init; }

    public IEnumerable<string>? Include { get; init; }

    public IEnumerable<ResponseToolDefinition>? Tools { get; init; }

    public Func<Tool, ResponseToolDefinition>? ToolDefinitionFactory { get; init; }

    public Func<ChatRequest, object?>? TextFactory { get; init; }

    public string? ReasoningSignatureProviderId { get; init; }

    public JsonElement[]? ContextManagement { get; init; }

    public bool NormalizeApprovals { get; init; } = true;
}
