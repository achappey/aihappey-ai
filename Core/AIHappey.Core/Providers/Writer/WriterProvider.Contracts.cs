using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Writer;

/// <summary>Optional advanced options. Normal agent and graph requests do not require provider metadata.</summary>
public sealed class WriterProviderMetadata
{
    [JsonPropertyName("inputs")]
    public Dictionary<string, IReadOnlyList<string>>? Inputs { get; init; }

    [JsonPropertyName("graph_ids")]
    public IReadOnlyList<string>? GraphIds { get; init; }

    [JsonPropertyName("subqueries")]
    public bool? Subqueries { get; init; }

    [JsonPropertyName("query_config")]
    public WriterGraphQueryConfig? QueryConfig { get; init; }

    [JsonPropertyName("poll_interval_ms")]
    public int? PollIntervalMs { get; init; }

    [JsonPropertyName("poll_timeout_seconds")]
    public int? PollTimeoutSeconds { get; init; }
}

public sealed class WriterGraphQueryConfig
{
    [JsonPropertyName("max_subquestions")] public int? MaxSubquestions { get; init; }
    [JsonPropertyName("search_weight")] public int? SearchWeight { get; init; }
    [JsonPropertyName("grounding_level")] public double? GroundingLevel { get; init; }
    [JsonPropertyName("max_snippets")] public int? MaxSnippets { get; init; }
    [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
    [JsonPropertyName("keyword_threshold")] public double? KeywordThreshold { get; init; }
    [JsonPropertyName("semantic_threshold")] public double? SemanticThreshold { get; init; }
    [JsonPropertyName("inline_citations")] public bool? InlineCitations { get; init; }
}

internal sealed record WriterResourceDescriptor(
    string Kind,
    string Id,
    string Slug,
    string Name,
    WriterApplication? Application = null,
    WriterGraph? Graph = null);

internal sealed class WriterApplication
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("inputs")] public List<WriterApplicationInput> Inputs { get; init; } = [];
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed class WriterApplicationInput
{
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("input_type")] public string InputType { get; init; } = "text";
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("required")] public bool Required { get; init; }
    [JsonPropertyName("options")] public JsonElement? Options { get; init; }
}

internal sealed class WriterGraph
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("created_at")] public DateTimeOffset? CreatedAt { get; init; }
}

internal sealed class WriterPage<T>
{
    [JsonPropertyName("data")] public List<T> Data { get; init; } = [];
    [JsonPropertyName("last_id")] public string? LastId { get; init; }
    [JsonPropertyName("has_more")] public bool HasMore { get; init; }
}

internal sealed class WriterApplicationJob
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("application_id")] public string? ApplicationId { get; init; }
    [JsonPropertyName("data")] public WriterApplicationResult? Data { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
}

internal sealed class WriterApplicationResult
{
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("suggestion")] public string Suggestion { get; init; } = string.Empty;
}

internal sealed class WriterFileResponse
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("status")] public string? Status { get; init; }
}

internal sealed record WriterPreparedFile(string Id, bool Temporary);
