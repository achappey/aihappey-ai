using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider
{
    private const string AgentModelPrefix = "agent/";

    private static bool IsContextualAIAgentModel(string? model)
        => TryResolveAgentId(model, out _);

    private async Task<AIResponse> ExecuteAgentUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryResolveAgentId(request.Model, out var agentId))
            throw new InvalidOperationException($"ContextualAI agent model '{request.Model}' must use 'contextualai/agent/{{agent_id}}'.");

        ApplyAuthHeader();

        var payload = BuildAgentPayload(request);
        var query = BuildAgentQueryString(request);
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, $"v1/agents/{Uri.EscapeDataString(agentId)}/query{query}", payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"ContextualAI agent query failed ({(int)response.StatusCode})."
                : $"ContextualAI agent query failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return CreateAgentUnifiedResponse(request, agentId, payload, document.RootElement.Clone());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteAgentUnifiedAsync(request, cancellationToken);

        foreach (var streamEvent in CreateSyntheticTextStream(request, response, "contextualai.agent.raw"))
            yield return streamEvent;
    }

    private static bool TryResolveAgentId(string? model, out string agentId)
    {
        agentId = string.Empty;
        var local = NormalizeContextualAIModel(model);

        if (!local.StartsWith(AgentModelPrefix, StringComparison.OrdinalIgnoreCase))
            return false;

        agentId = local[AgentModelPrefix.Length..].Trim('/');
        return !string.IsNullOrWhiteSpace(agentId);
    }

    private JsonObject BuildAgentPayload(AIRequest request)
    {
        var payload = ExtractProviderOptions(request.Metadata) is { } providerOptions
            ? JsonElementObjectToJsonObject(providerOptions)
            : [];

        payload["messages"] = BuildContextualAIMessages(request, includeSystem: true, includeKnowledge: true);

        if (!payload.ContainsKey("stream"))
            payload["stream"] = false;

        if (request.Metadata?.GetProviderOption<string>(GetIdentifier(), "conversation_id") is { Length: > 0 } conversationId)
            payload["conversation_id"] = conversationId;

        if (request.Metadata?.GetProviderOption<string>(GetIdentifier(), "llm_model_id") is { Length: > 0 } llmModelId)
            payload["llm_model_id"] = llmModelId;

        AddProviderOption(payload, request, "structured_output");
        AddProviderOption(payload, request, "documents_filters");
        AddProviderOption(payload, request, "override_configuration");

        if (request.Temperature is not null || request.TopP is not null || request.MaxOutputTokens is not null)
        {
            var overrides = payload.TryGetPropertyValue("override_configuration", out var node) && node is JsonObject objectNode
                ? objectNode
                : [];

            if (request.Temperature is not null && !overrides.ContainsKey("temperature"))
                overrides["temperature"] = request.Temperature.Value;
            if (request.TopP is not null && !overrides.ContainsKey("top_p"))
                overrides["top_p"] = request.TopP.Value;
            if (request.MaxOutputTokens is not null && !overrides.ContainsKey("max_new_tokens"))
                overrides["max_new_tokens"] = request.MaxOutputTokens.Value;

            payload["override_configuration"] = overrides;
        }

        return payload;
    }

    private string BuildAgentQueryString(AIRequest request)
    {
        var query = new List<string>();

        if (request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "retrievals_only") is { } retrievalsOnly)
            query.Add($"retrievals_only={retrievalsOnly.ToString().ToLowerInvariant()}");

        if (request.Metadata?.GetProviderOption<bool?>(GetIdentifier(), "include_retrieval_content_text") is { } includeText)
            query.Add($"include_retrieval_content_text={includeText.ToString().ToLowerInvariant()}");

        return query.Count == 0 ? string.Empty : "?" + string.Join("&", query);
    }

    private AIResponse CreateAgentUnifiedResponse(AIRequest request, string agentId, JsonObject payload, JsonElement root)
    {
        var text = ExtractAgentResponseText(root);
        var metadata = new Dictionary<string, object?>
        {
            ["contextualai.agent"] = true,
            ["contextualai.agent.id"] = agentId,
            ["contextualai.agent.request.payload"] = JsonSerializer.SerializeToElement(payload, ContextualAIJson),
            ["contextualai.agent.raw"] = root.Clone(),
            ["contextualai.agent.conversation_id"] = root.TryGetString("conversation_id"),
            ["contextualai.agent.message_id"] = root.TryGetString("message_id"),
            ["contextualai.agent.retrieval_contents"] = CloneProperty(root, "retrieval_contents"),
            ["contextualai.agent.attributions"] = CloneProperty(root, "attributions"),
            ["contextualai.agent.groundedness_scores"] = CloneProperty(root, "groundedness_scores")
        };

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier()) 
                ?? $"{AgentModelPrefix}{agentId}".ToModelId(GetIdentifier()),
            Status = "completed",
            Metadata = metadata,
            Output = new AIOutput
            {
                Items =
                [
                    new AIOutputItem
                    {
                        Type = "message",
                        Role = "assistant",
                        Content =
                        [
                            new AITextContentPart
                            {
                                Type = "text",
                                Text = text,
                                Metadata = new Dictionary<string, object?>
                                {
                                    ["contextualai.agent.raw"] = root.Clone(),
                                    ["contextualai.agent.attributions"] = CloneProperty(root, "attributions")
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["contextualai.agent.raw"] = root.Clone()
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["contextualai.agent.raw"] = root.Clone(),
                    ["contextualai.agent.retrieval_contents"] = CloneProperty(root, "retrieval_contents")
                }
            }
        };
    }

    private static string ExtractAgentResponseText(JsonElement root)
    {
        if (root.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object)
            return message.TryGetString("content") ?? string.Empty;

        return root.TryGetString("response")
            ?? root.TryGetString("text")
            ?? string.Empty;
    }
}
