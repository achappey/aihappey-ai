using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Agentics;

public partial class AgenticsProvider
{
    private const string AgenticsAgentModelId = "agent";
    private const string AgenticsAgentEndpoint = "v1/agent/message";

    private static readonly JsonSerializerOptions AgenticsAgentJson = new(JsonSerializerOptions.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    private static bool IsAgenticsAgentModel(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return false;

        var localModel = model.Trim();
        if (localModel.StartsWith("agentics/", StringComparison.OrdinalIgnoreCase))
            localModel = localModel["agentics/".Length..];

        return string.Equals(localModel, AgenticsAgentModelId, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<AIResponse> ExecuteAgentUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var payload = BuildAgenticsAgentPayload(request);
        using var httpRequest = CreateAgenticsAgentRequest(payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"Agentics agent request failed ({(int)response.StatusCode})."
                : $"Agentics agent request failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return CreateAgenticsAgentUnifiedResponse(request, payload, document.RootElement.Clone());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamAgentUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var timestamp = DateTimeOffset.UtcNow;
        var response = await ExecuteAgentUnifiedAsync(request, cancellationToken);
        var eventId = request.Id ?? $"agentics_agent_{Guid.NewGuid():N}";
        var metadata = response.Metadata ?? [];
        var text = ExtractAgenticsAgentResponseText(response);
        var providerMetadata = CreateAgenticsAgentLooseProviderMetadata(response);

        if (!string.IsNullOrEmpty(text))
        {
            yield return CreateAgenticsAgentStreamEvent(
                eventId,
                "text-start",
                new AITextStartEventData { ProviderMetadata = providerMetadata },
                timestamp,
                metadata);

            yield return CreateAgenticsAgentStreamEvent(
                eventId,
                "text-delta",
                new AITextDeltaEventData
                {
                    Delta = text,
                    ProviderMetadata = providerMetadata
                },
                DateTimeOffset.UtcNow,
                metadata);

            yield return CreateAgenticsAgentStreamEvent(
                eventId,
                "text-end",
                new AITextEndEventData { ProviderMetadata = providerMetadata },
                DateTimeOffset.UtcNow,
                metadata);
        }

        yield return CreateAgenticsAgentFinishEvent(eventId, request, response, DateTimeOffset.UtcNow, metadata);
    }

    private static JsonObject BuildAgenticsAgentPayload(AIRequest request)
    {
        var providerOptions = ExtractAgenticsProviderOptions(request.Metadata, "agentics");
        var payload = providerOptions is null ? [] : JsonElementObjectToJsonObject(providerOptions.Value);

        var message = ExtractAgenticsAgentMessage(request);
        if (string.IsNullOrWhiteSpace(message)
            && payload.TryGetPropertyValue("message", out var messageNode)
            && messageNode is JsonValue messageValue
            && messageValue.TryGetValue<string>(out var providerMessage))
        {
            message = providerMessage;
        }

        if (string.IsNullOrWhiteSpace(message))
            throw new ArgumentException("Agentics agent requests require a user message or input text.", nameof(request));

        payload["message"] = message;

        if (request.MaxOutputTokens is not null && !payload.ContainsKey("maxTokens"))
            payload["maxTokens"] = request.MaxOutputTokens.Value;

        if (!payload.ContainsKey("context"))
        {
            var context = BuildAgenticsAgentContext(request);
            if (context.Count > 0)
                payload["context"] = context;
        }

        if (!payload.ContainsKey("tools"))
        {
            var tools = BuildAgenticsAgentTools(request);
            if (tools.Count > 0)
                payload["tools"] = tools;
        }

        return payload;
    }

    private HttpRequestMessage CreateAgenticsAgentRequest(JsonObject payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, AgenticsAgentEndpoint)
        {
            Content = new StringContent(payload.ToJsonString(AgenticsAgentJson), Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        return request;
    }

    private AIResponse CreateAgenticsAgentUnifiedResponse(AIRequest request, JsonObject payload, JsonElement root)
    {
        var text = ExtractAgenticsAgentResponseText(root);
        var usage = CreateAgenticsAgentUsage(root);
        var metadata = CreateAgenticsAgentMetadata(request, payload, root);
        var model = request.Model ?? AgenticsAgentModelId;

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = model,
            Status = string.IsNullOrWhiteSpace(text) && TryExtractAgenticsAgentError(root) is not null ? "failed" : "completed",
            Usage = usage,
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
                                    ["agentics.agent.raw"] = root.Clone(),
                                    ["agentics.agent.tools_used"] = CloneProperty(root, "toolsUsed")
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["agentics.agent.raw"] = root.Clone(),
                            ["agentics.agent.tools_used"] = CloneProperty(root, "toolsUsed")
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["agentics.agent.raw"] = root.Clone(),
                    ["agentics.agent.tools_used"] = CloneProperty(root, "toolsUsed")
                }
            }
        };
    }

    private Dictionary<string, object?> CreateAgenticsAgentMetadata(AIRequest request, JsonObject payload, JsonElement root)
    {
        var metadata = new Dictionary<string, object?>
        {
            ["agentics.agent"] = true,
            ["agentics.agent.model"] = request.Model ?? AgenticsAgentModelId,
            ["agentics.agent.request.payload"] = JsonSerializer.SerializeToElement(payload, AgenticsAgentJson),
            ["agentics.agent.raw"] = root.Clone(),
            ["agentics.agent.response"] = CloneProperty(root, "response"),
            ["agentics.agent.tokens_used"] = ExtractAgenticsAgentTokensUsed(root),
            ["agentics.agent.tools_used"] = CloneProperty(root, "toolsUsed")
        };

        if (TryExtractAgenticsAgentError(root) is { Length: > 0 } error)
            metadata["agentics.agent.error"] = error;

        return metadata;
    }

    private static AIStreamEvent CreateAgenticsAgentStreamEvent(
        string eventId,
        string type,
        object data,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
        => new()
        {
            ProviderId = "agentics",
            Event = new AIEventEnvelope
            {
                Type = type,
                Id = eventId,
                Timestamp = timestamp,
                Data = data
            },
            Metadata = metadata
        };

    private static AIStreamEvent CreateAgenticsAgentFinishEvent(
        string eventId,
        AIRequest request,
        AIResponse response,
        DateTimeOffset timestamp,
        Dictionary<string, object?> metadata)
    {
        var totalTokens = ExtractAgenticsAgentTokensUsed(response.Usage);
        var model = response.Model ?? request.Model ?? AgenticsAgentModelId;

        return CreateAgenticsAgentStreamEvent(
            eventId,
            "finish",
            new AIFinishEventData
            {
                FinishReason = string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase) ? "error" : "stop",
                Model = model,
                TotalTokens = totalTokens,
                MessageMetadata = AIFinishMessageMetadata.Create(
                    model,
                    timestamp,
                    response.Usage,
                    totalTokens: totalTokens,
                    additionalProperties: new Dictionary<string, object?>
                    {
                        ["agentics"] = metadata.TryGetValue("agentics.agent.raw", out var raw) ? raw : null
                    })
            },
            timestamp,
            metadata);
    }

    private static Dictionary<string, object>? CreateAgenticsAgentLooseProviderMetadata(AIResponse response)
    {
        var metadata = new Dictionary<string, object>();

        if (response.Metadata?.TryGetValue("agentics.agent.raw", out var raw) == true && raw is not null)
            metadata["raw"] = raw;

        if (response.Metadata?.TryGetValue("agentics.agent.tools_used", out var toolsUsed) == true && toolsUsed is not null)
            metadata["toolsUsed"] = toolsUsed;

        if (response.Usage is not null)
            metadata["usage"] = response.Usage;

        return metadata.Count == 0 ? null : metadata;
    }

    private static string ExtractAgenticsAgentResponseText(AIResponse response)
        => string.Join("\n", (response.Output?.Items ?? [])
            .Where(item => string.Equals(item.Type, "message", StringComparison.OrdinalIgnoreCase))
            .SelectMany(item => item.Content ?? [])
            .OfType<AITextContentPart>()
            .Select(part => part.Text)
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static string ExtractAgenticsAgentResponseText(JsonElement root)
        => TryGetAgenticsAgentString(root, "response")
           ?? TryGetAgenticsAgentString(root, "message")
           ?? TryGetAgenticsAgentString(root, "text")
           ?? TryGetAgenticsAgentString(root, "output")
           ?? string.Empty;

    private static string? TryExtractAgenticsAgentError(JsonElement root)
    {
        if (TryGetAgenticsAgentString(root, "error") is { Length: > 0 } error)
            return error;

        if (root.TryGetProperty("error", out var errorEl)
            && errorEl.ValueKind == JsonValueKind.Object
            && TryGetAgenticsAgentString(errorEl, "message") is { Length: > 0 } message)
            return message;

        return null;
    }

    private static JsonElement? CreateAgenticsAgentUsage(JsonElement root)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            return usage.Clone();

        var tokensUsed = ExtractAgenticsAgentTokensUsed(root);
        if (tokensUsed is null)
            return null;

        return JsonSerializer.SerializeToElement(new
        {
            tokensUsed,
            total_tokens = tokensUsed,
            totalTokens = tokensUsed
        }, AgenticsAgentJson);
    }

    private static int? ExtractAgenticsAgentTokensUsed(object? value)
    {
        switch (value)
        {
            case JsonElement json:
                return ExtractAgenticsAgentTokensUsed(json);
            case Dictionary<string, object?> dictionary:
                return dictionary.TryGetValue("tokensUsed", out var tokensUsed)
                    ? ConvertAgenticsAgentInt(tokensUsed)
                    : dictionary.TryGetValue("totalTokens", out var totalTokens)
                        ? ConvertAgenticsAgentInt(totalTokens)
                        : null;
            default:
                return null;
        }
    }

    private static int? ExtractAgenticsAgentTokensUsed(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (root.TryGetProperty("tokensUsed", out var tokensUsed) && tokensUsed.ValueKind == JsonValueKind.Number && tokensUsed.TryGetInt32(out var direct))
            return direct;

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            foreach (var propertyName in new[] { "tokensUsed", "totalTokens", "total_tokens" })
            {
                if (usage.TryGetProperty(propertyName, out var value)
                    && value.ValueKind == JsonValueKind.Number
                    && value.TryGetInt32(out var usageTokens))
                    return usageTokens;
            }
        }

        return null;
    }

    private static int? ConvertAgenticsAgentInt(object? value)
        => value switch
        {
            int intValue => intValue,
            long longValue when longValue >= int.MinValue && longValue <= int.MaxValue => (int)longValue,
            JsonElement json when json.ValueKind == JsonValueKind.Number && json.TryGetInt32(out var jsonValue) => jsonValue,
            string stringValue when int.TryParse(stringValue, out var parsed) => parsed,
            _ => null
        };

    private static string ExtractAgenticsAgentMessage(AIRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Input?.Text))
            return request.Input.Text!;

        var selectedItem = (request.Input?.Items ?? [])
            .LastOrDefault(item => string.Equals(item.Role, "user", StringComparison.OrdinalIgnoreCase)
                                   && !string.IsNullOrWhiteSpace(ExtractAgenticsAgentText(item.Content)))
            ?? (request.Input?.Items ?? [])
                .LastOrDefault(item => !string.IsNullOrWhiteSpace(ExtractAgenticsAgentText(item.Content)));

        if (selectedItem is not null)
            return ExtractAgenticsAgentText(selectedItem.Content);

        return request.Instructions ?? string.Empty;
    }

    private static JsonArray BuildAgenticsAgentContext(AIRequest request)
    {
        var context = new JsonArray();
        var items = request.Input?.Items ?? [];
        if (items.Count == 0)
            return context;

        var latestUserIndex = -1;
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (string.Equals(items[index].Role, "user", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(ExtractAgenticsAgentText(items[index].Content)))
            {
                latestUserIndex = index;
                break;
            }
        }

        for (var index = 0; index < items.Count; index++)
        {
            if (index == latestUserIndex)
                continue;

            var item = items[index];
            var text = ExtractAgenticsAgentText(item.Content);
            if (string.IsNullOrWhiteSpace(text))
                continue;

            context.Add(new JsonObject
            {
                ["role"] = string.IsNullOrWhiteSpace(item.Role) ? "user" : item.Role,
                ["content"] = text
            });
        }

        return context;
    }

    private static JsonArray BuildAgenticsAgentTools(AIRequest request)
    {
        var tools = new JsonArray();
        foreach (var toolName in (request.Tools ?? [])
                     .Select(tool => tool.Name)
                     .Where(name => !string.IsNullOrWhiteSpace(name))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            tools.Add(toolName);
        }

        return tools;
    }

    private static string ExtractAgenticsAgentText(IEnumerable<AIContentPart>? parts)
        => string.Join("\n", (parts ?? [])
            .Select(part => part switch
            {
                AITextContentPart text => text.Text,
                AIReasoningContentPart reasoning => reasoning.Text,
                AIFileContentPart file when file.Data is string text => text,
                _ => null
            })
            .Where(text => !string.IsNullOrWhiteSpace(text)));

    private static JsonElement? ExtractAgenticsProviderOptions(Dictionary<string, object?>? metadata, string providerId)
    {
        if (metadata is null || !metadata.TryGetValue(providerId, out var raw) || raw is null)
            return null;

        var element = raw switch
        {
            JsonElement json => json.Clone(),
            JsonObject jsonObject => JsonSerializer.SerializeToElement(jsonObject, AgenticsAgentJson),
            Dictionary<string, object?> dictionary => JsonSerializer.SerializeToElement(dictionary, AgenticsAgentJson),
            _ => JsonSerializer.SerializeToElement(raw, AgenticsAgentJson)
        };

        return element.ValueKind == JsonValueKind.Object
            ? element
            : null;
    }

    private static JsonObject JsonElementObjectToJsonObject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        return JsonNode.Parse(element.GetRawText()) as JsonObject ?? [];
    }

    private static JsonElement? CloneProperty(JsonElement root, string propertyName)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(propertyName, out var property)
            ? property.Clone()
            : null;

    private static string? TryGetAgenticsAgentString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
            _ => null
        };
    }
}
