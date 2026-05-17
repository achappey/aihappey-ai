using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider
{
    private const string GenerateEndpoint = "v1/generate";

    private async Task<AIResponse> ExecuteGenerateUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        ApplyAuthHeader();

        var payload = BuildGeneratePayload(request);
        using var httpRequest = CreateJsonRequest(HttpMethod.Post, GenerateEndpoint, payload);
        using var response = await _client.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(string.IsNullOrWhiteSpace(body)
                ? $"ContextualAI generate request failed ({(int)response.StatusCode})."
                : $"ContextualAI generate request failed ({(int)response.StatusCode}): {body}");

        using var document = JsonDocument.Parse(body);
        return CreateGenerateUnifiedResponse(request, payload, document.RootElement.Clone());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamGenerateUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await ExecuteGenerateUnifiedAsync(request, cancellationToken);

        foreach (var streamEvent in CreateSyntheticTextStream(request, response, "contextualai.generate.raw"))
            yield return streamEvent;
    }

    private JsonObject BuildGeneratePayload(AIRequest request)
    {
        var payload = ExtractProviderOptions(request.Metadata) is { } providerOptions
            ? JsonElementObjectToJsonObject(providerOptions)
            : [];

        payload["model"] = ResolveGenerateModel(request.Model);
        payload["messages"] = BuildContextualAIMessages(request, includeSystem: false, includeKnowledge: false);
        payload["knowledge"] = ResolveKnowledge(request, payload);

        if (!payload.ContainsKey("system_prompt") && !string.IsNullOrWhiteSpace(request.Instructions))
            payload["system_prompt"] = request.Instructions;

        if (request.Temperature is not null && !payload.ContainsKey("temperature"))
            payload["temperature"] = request.Temperature.Value;

        if (request.TopP is not null && !payload.ContainsKey("top_p"))
            payload["top_p"] = request.TopP.Value;

        if (request.MaxOutputTokens is not null && !payload.ContainsKey("max_new_tokens"))
            payload["max_new_tokens"] = request.MaxOutputTokens.Value;

        return payload;
    }

    private AIResponse CreateGenerateUnifiedResponse(AIRequest request, JsonObject payload, JsonElement root)
    {
        var text = root.TryGetString("response") ?? string.Empty;
        var metadata = new Dictionary<string, object?>
        {
            ["contextualai.generate"] = true,
            ["contextualai.generate.model"] = ResolveGenerateModel(request.Model),
            ["contextualai.generate.request.payload"] = JsonSerializer.SerializeToElement(payload, ContextualAIJson),
            ["contextualai.generate.raw"] = root.Clone()
        };

        return new AIResponse
        {
            ProviderId = GetIdentifier(),
            Model = request.Model?.ToModelId(GetIdentifier())
                ?? ResolveGenerateModel(request.Model).ToModelId(GetIdentifier()),
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
                                    ["contextualai.generate.raw"] = root.Clone()
                                }
                            }
                        ],
                        Metadata = new Dictionary<string, object?>
                        {
                            ["contextualai.generate.raw"] = root.Clone()
                        }
                    }
                ],
                Metadata = new Dictionary<string, object?>
                {
                    ["contextualai.generate.raw"] = root.Clone()
                }
            }
        };
    }

    private static string ResolveGenerateModel(string? model)
    {
        var local = NormalizeContextualAIModel(model);
        return string.IsNullOrWhiteSpace(local) ? "v2" : local;
    }

    private static JsonArray ResolveKnowledge(AIRequest request, JsonObject payload)
    {
        if (payload.TryGetPropertyValue("knowledge", out var existing) && existing is JsonArray existingArray)
            return existingArray;

        var metadataKnowledge = request.Metadata?.GetProviderOption<List<string>>("contextualai", "knowledge")
            ?? request.Metadata?.GetProviderOption<string[]>("contextualai", "knowledge")?.ToList();

        if (metadataKnowledge is not null)
        {
            return [.. metadataKnowledge
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => (JsonNode?)JsonValue.Create(value))
                    .ToArray()];
        }

        return new JsonArray(
            [.. (request.Input?.Items ?? [])
                .Where(item =>
                    string.Equals(item.Role, "knowledge", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.Type, "knowledge", StringComparison.OrdinalIgnoreCase))
                .Select(item => ExtractText(item.Content))
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => (JsonNode?)JsonValue.Create(text))]
        );
    }
}
