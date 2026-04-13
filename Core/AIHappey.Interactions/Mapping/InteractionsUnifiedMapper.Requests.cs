using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Interactions.Mapping;

public static partial class InteractionsUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this InteractionRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model ?? request.Agent,
            Instructions = request.SystemInstruction,
            Input = request.Input is null ? null : ToUnifiedInput(request.Input, providerId),
            Temperature = request.GenerationConfig?.Temperature,
            TopP = request.GenerationConfig?.TopP,
            MaxOutputTokens = request.GenerationConfig?.MaxOutputTokens,
            Stream = request.Stream,
            ToolChoice = CloneIfJsonElement(request.GenerationConfig?.ToolChoice),
            ResponseFormat = CloneIfJsonElement(request.ResponseFormat),
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    public static InteractionRequest ToInteractionRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();
        var storedModel = ExtractValue<string>(metadata, "interactions.request.model");
        var storedAgent = ExtractValue<string>(metadata, "interactions.request.agent");
        var providerConfig = metadata.TryGetValue(providerId, out object? config) ? config.ToDictionary() : null;
        var generationConfig = providerConfig != null ? ExtractObject<InteractionGenerationConfig>(providerConfig,
            "generation_config") : new InteractionGenerationConfig();
        generationConfig ??= new InteractionGenerationConfig();
        
        generationConfig.Temperature = request.Temperature ?? generationConfig.Temperature;
        generationConfig.TopP = request.TopP ?? generationConfig.TopP;
        generationConfig.MaxOutputTokens = request.MaxOutputTokens ?? generationConfig.MaxOutputTokens;
        generationConfig.ToolChoice = CloneIfJsonElement(request.ToolChoice) ?? generationConfig.ToolChoice;

        return new InteractionRequest
        {
            Model = string.IsNullOrWhiteSpace(storedAgent) || !string.IsNullOrWhiteSpace(storedModel)
                ? request.Model ?? storedModel
                : storedModel,
            Agent = storedAgent,
            Metadata = request.Metadata,
            Input = request.Input is null ? null : ToInteractionInput(request.Input, providerId),
            SystemInstruction = request.Instructions,
            Tools = request.Tools?.Select(ToInteractionTool).ToList(),
            ResponseFormat = CloneIfJsonElement(request.ResponseFormat) ?? ExtractObject<object>(metadata, "interactions.request.response_format"),
            ResponseMimeType = ExtractValue<string>(metadata, "interactions.request.response_mime_type"),
            Stream = request.Stream,
            Store = false,
            Background = ExtractValue<bool?>(metadata, "interactions.request.background"),
            GenerationConfig = string.IsNullOrWhiteSpace(storedAgent) ? generationConfig : null,
            AgentConfig = string.IsNullOrWhiteSpace(storedAgent)
                ? null
                : ExtractObject<InteractionAgentConfig>(metadata, "interactions.request.agent_config"),
            PreviousInteractionId = ExtractValue<string>(metadata, "interactions.request.previous_interaction_id"),
            ResponseModalities = ExtractObject<List<string>>(metadata, "interactions.request.response_modalities"),
            ServiceTier = ExtractValue<string>(metadata, "interactions.request.service_tier"),
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "interactions.request.additional_properties")
        };
    }

    private static AIInput ToUnifiedInput(InteractionsInput input, string providerId)
    {
        if (input.IsText)
            return new AIInput { Text = input.Text };

        if (input.IsTurns)
        {
            return new AIInput
            {
                Items = input.Turns!.Select(turn => ToUnifiedInputItem(turn, providerId)).ToList(),
                Metadata = new Dictionary<string, object?> { ["interactions.input.shape"] = "turns" }
            };
        }

        if (input.IsSingleContent || input.IsContent)
        {
            var content = input.IsSingleContent ? [input.SingleContent!] : input.Content!;
            return new AIInput
            {
                Items =
                [
                    new AIInputItem
                    {
                        Type = "message",
                        Role = "user",
                        Content = ToUnifiedContentParts(content, providerId).ToList(),
                        Metadata = new Dictionary<string, object?>
                        {
                            ["interactions.input.shape"] = input.IsSingleContent ? "single_content" : "content_array",
                            ["interactions.input.raw"] = JsonSerializer.SerializeToElement(input, Json)
                        }
                    }
                ]
            };
        }

        return new AIInput
        {
            Metadata = new Dictionary<string, object?> { ["interactions.input.raw"] = CloneJsonElement(input.Raw) }
        };
    }

    private static AIInputItem ToUnifiedInputItem(InteractionTurn turn, string providerId)
    {
        var parts = turn.Content?.IsText == true
            ? new List<AIContentPart> { new AITextContentPart { Type = "text", Text = turn.Content.Text ?? string.Empty } }
            : ToUnifiedContentParts(turn.Content?.Parts, providerId).ToList();

        return new AIInputItem
        {
            Type = "message",
            Role = NormalizeUnifiedRole(turn.Role),
            Content = parts,
            Metadata = new Dictionary<string, object?>
            {
                ["interactions.turn.role"] = turn.Role,
                ["interactions.turn.raw"] = JsonSerializer.SerializeToElement(turn, Json)
            }
        };
    }

    private static InteractionsInput ToInteractionInput(AIInput input, string providerId)
    {
        if (!string.IsNullOrWhiteSpace(input.Text))
            return new InteractionsInput(input.Text);

        var items = input.Items ?? [];
        if (items.Count == 0)
            return new InteractionsInput();

        var turns = items.Select(item => ToInteractionTurn(item)).ToList();
        return new InteractionsInput(turns, true);
    }

    private static InteractionTurn ToInteractionTurn(AIInputItem item)
    {
        var content = new List<InteractionContent>();
        foreach (var part in item.Content ?? [])
        {
            var mapped = ToInteractionContent(part);
            if (mapped is InteractionThoughtContent thought
                && string.IsNullOrWhiteSpace(FlattenContentText(thought.Summary))
                && !string.IsNullOrWhiteSpace(thought.Signature))
            {
                var previousThought = content.OfType<InteractionThoughtContent>().LastOrDefault();
                if (previousThought is not null
                    && (string.IsNullOrWhiteSpace(previousThought.Signature)
                        || string.Equals(previousThought.Signature, thought.Signature, StringComparison.Ordinal)))
                {
                    previousThought.Signature ??= thought.Signature;
                    continue;
                }
            }

            if (mapped is not null)
                content.Add(mapped);
        }

        InteractionTurnContent turnContent;
        if (content.Count == 1 && content[0] is InteractionTextContent text && text.Annotations is null)
            turnContent = new InteractionTurnContent(text.Text ?? string.Empty);
        else
            turnContent = new InteractionTurnContent(content);

        return new InteractionTurn
        {
            Role = NormalizeInteractionRole(item.Role),
            Content = turnContent
        };
    }
}
