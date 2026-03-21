using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Mistral;

public partial class MistralProvider
{
    private const string AgentModelPrefix = "agent/";

    private ConversationTarget ResolveConversationTarget(string? model)
    {
        var normalized = NormalizeMistralModelId(model);
        if (string.IsNullOrWhiteSpace(normalized))
            return new ConversationTarget(null, null);

        if (normalized.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var agentId = normalized[AgentModelPrefix.Length..].Trim();
            if (!string.IsNullOrWhiteSpace(agentId))
                return new ConversationTarget(null, agentId);
        }

        return new ConversationTarget(normalized, null);
    }

    private static void ApplyConversationTarget(JsonObject payload, ConversationTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.AgentId))
        {
            payload["agent_id"] = target.AgentId;
            return;
        }

        if (!string.IsNullOrWhiteSpace(target.Model))
            payload["model"] = target.Model;
    }

    private string NormalizeReportedModel(string? upstreamModel, ConversationTarget target)
    {
        if (!string.IsNullOrWhiteSpace(target.AgentId))
            return target.ExposedModelId;

        var normalized = NormalizeMistralModelId(upstreamModel);
        return string.IsNullOrWhiteSpace(normalized)
            ? target.ExposedModelId
            : normalized;
    }

    private string NormalizeMistralModelId(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return string.Empty;

        var trimmed = model.Trim();
        var providerPrefix = GetIdentifier() + "/";

        if (trimmed.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return trimmed.SplitModelId().Model;

        return trimmed;
    }

    private async Task<IReadOnlyList<MistralAgentDefinition>> ListAgentsAsync(CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "/v1/agents");
        using var resp = await _client.SendAsync(req, cancellationToken);
        if (!resp.IsSuccessStatusCode)
            return [];

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<MistralAgentDefinition>>(body, JsonSerializerOptions.Web) ?? [];
    }

    private sealed record ConversationTarget(string? Model, string? AgentId)
    {
        public string ExposedModelId => !string.IsNullOrWhiteSpace(AgentId)
            ? $"{MistralProvider.AgentModelPrefix}{AgentId}"
            : Model ?? "mistral";
    }

    private sealed class MistralAgentDefinition
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("model")]
        public string? Model { get; set; }

        [JsonPropertyName("created_at")]
        public DateTimeOffset? CreatedAt { get; set; }
    }
}
