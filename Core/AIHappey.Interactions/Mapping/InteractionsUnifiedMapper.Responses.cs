using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    public static AIResponse ToUnifiedResponse(this Interaction response, string providerId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var outputItems = ToUnifiedOutputItems(response, providerId).ToList();

        return new AIResponse
        {
            ProviderId = providerId,
            Model = response.Model ?? response.Agent,
            Status = response.Status,
            Usage = response.Usage,
            Output = outputItems.Count == 0 ? null : new AIOutput { Items = outputItems },
            Metadata = response.Metadata
        };
    }

    public static Interaction ToInteraction(this AIResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var metadata = response.Metadata ?? [];
        var outputParts = ToInteractionOutputContent(response.Output).ToList();

        return new Interaction
        {
            Id = ExtractValue<string>(metadata, "interactions.response.id") ?? Guid.NewGuid().ToString("N"),
            Object = ExtractValue<string>(metadata, "interactions.response.object") ?? "interaction",
            Model = response.Model,
            Agent = ExtractValue<string>(metadata, "interactions.response.agent"),
            Status = response.Status,
            Created = ExtractValue<string>(metadata, "interactions.response.created"),
            Updated = ExtractValue<string>(metadata, "interactions.response.updated"),
            Role = ExtractValue<string>(metadata, "interactions.response.role") ?? "model",
            Outputs = outputParts.Count == 0 ? null : outputParts,
            Usage = DeserializeFromObject<InteractionUsage>(response.Usage),
            SystemInstruction = ExtractValue<string>(metadata, "interactions.response.system_instruction"),
            Tools = ExtractObject<List<InteractionTool>>(metadata, "interactions.response.tools"),
            ResponseModalities = ExtractObject<List<string>>(metadata, "interactions.response.response_modalities"),
            ResponseFormat = ExtractObject<object>(metadata, "interactions.response.response_format"),
            ResponseMimeType = ExtractValue<string>(metadata, "interactions.response.response_mime_type"),
            PreviousInteractionId = ExtractValue<string>(metadata, "interactions.response.previous_interaction_id"),
            ServiceTier = ExtractValue<string>(metadata, "interactions.response.service_tier"),
            Input = ExtractObject<InteractionsInput>(metadata, "interactions.response.input"),
            GenerationConfig = ExtractObject<InteractionGenerationConfig>(metadata, "interactions.response.generation_config"),
            AgentConfig = ExtractObject<InteractionAgentConfig>(metadata, "interactions.response.agent_config"),
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "interactions.response.additional_properties")
        };
    }

    private static IEnumerable<AIOutputItem> ToUnifiedOutputItems(Interaction response, string providerId)
    {
        var parts = ToUnifiedContentParts(response.Outputs, providerId).ToList();
        if (parts.Count == 0)
            yield break;

        yield return new AIOutputItem
        {
            Type = "message",
            Role = NormalizeUnifiedRole(response.Role),
            Content = parts,
            Metadata = new Dictionary<string, object?>
            {
                ["interactions.outputs.raw"] = JsonSerializer.SerializeToElement(response.Outputs, Json),
                ["interactions.role.raw"] = response.Role
            }
        };
    }

    private static IEnumerable<InteractionContent> ToInteractionOutputContent(AIOutput? output)
    {
        foreach (var item in output?.Items ?? [])
        {
            foreach (var part in item.Content ?? [])
            {
                var mapped = ToInteractionContent(part);
                if (mapped is not null)
                    yield return mapped;
            }
        }
    }
}
