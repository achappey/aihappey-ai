using System.Text.Json;
using AIHappey.Unified.Models;

namespace AIHappey.Messages.Mapping;

public static partial class MessagesUnifiedMapper
{
    public static AIRequest ToUnifiedRequest(this MessagesRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        return new AIRequest
        {
            ProviderId = providerId,
            Model = request.Model,
            Instructions = FlattenContentText(request.System),
            Input = new AIInput
            {
                Items = request.Messages.Select(message => ToUnifiedInputItem(message, providerId)).ToList(),
                Metadata = new Dictionary<string, object?>
                {
                    ["messages.input.raw"] = request.Messages.Count == 0
                        ? null
                        : JsonSerializer.SerializeToElement(request.Messages, Json)
                }
            },
            Temperature = request.Temperature,
            TopP = request.TopP,
            MaxOutputTokens = request.MaxTokens,
            Stream = request.Stream,
            ParallelToolCalls = request.ToolChoice?.DisableParallelToolUse is bool disable ? !disable : null,
            ToolChoice = request.ToolChoice is null ? null : JsonSerializer.SerializeToElement(request.ToolChoice, Json),
            Tools = request.Tools?.Select(ToUnifiedTool).ToList(),
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    public static MessagesRequest ToMessagesRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? new Dictionary<string, object?>();
        var system = ExtractObject<MessagesContent>(metadata, "messages.request.system")
                     ?? (!string.IsNullOrWhiteSpace(request.Instructions) ? new MessagesContent(request.Instructions) : null);

        var metadataObj = JsonSerializer.Deserialize<MessagesRequestMetadata>(
            JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web)
        );

        return new MessagesRequest
        {
            Model = request.Model,
            MaxTokens = request.MaxOutputTokens,
            Messages = ToMessageParams(request.Input?.Items ?? []).ToList(),
            CacheControl = ExtractObject<CacheControlEphemeral>(metadata, "messages.request.cache_control"),
            Container = ExtractValue<string>(metadata, "messages.request.container"),
            InferenceGeo = ExtractValue<string>(metadata, "messages.request.inference_geo"),
            Metadata = metadataObj,
            OutputConfig = ExtractObject<MessagesOutputConfig>(metadata, "messages.request.output_config"),
            ServiceTier = ExtractValue<string>(metadata, "messages.request.service_tier"),
            StopSequences = ExtractObject<List<string>>(metadata, "messages.request.stop_sequences"),
            Stream = request.Stream,
            System = system,
            Temperature = request.Temperature,
            Thinking = ExtractObject<MessagesThinkingConfig>(metadata, "messages.request.thinking"),
            ToolChoice = request.ToolChoice == null ? new MessageToolChoice()
            {
                Type = "auto",
                DisableParallelToolUse = false

            } : new MessageToolChoice()
            {
                Type = request.ToolChoice?.ToString()!,
                DisableParallelToolUse = false
            },
            Tools = request.Tools?.Select(ToMessageTool).ToList(),
            TopK = ExtractValue<int?>(metadata, "messages.request.top_k"),
            TopP = request.TopP,
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.request.unmapped")
        };
    }

    private static Dictionary<string, object?> BuildUnifiedRequestMetadata(MessagesRequest request)
    {
        var raw = JsonSerializer.SerializeToElement(request, Json);
        var metadata = new Dictionary<string, object?>
        {
            ["messages.request.raw"] = raw,
            ["messages.request.cache_control"] = request.CacheControl,
            ["messages.request.container"] = request.Container,
            ["messages.request.inference_geo"] = request.InferenceGeo,
            ["messages.request.metadata"] = request.Metadata,
            ["messages.request.output_config"] = request.OutputConfig,
            ["messages.request.service_tier"] = request.ServiceTier,
            ["messages.request.stop_sequences"] = request.StopSequences,
            ["messages.request.system"] = request.System,
            ["messages.request.thinking"] = request.Thinking,
            ["messages.request.tool_choice"] = request.ToolChoice,
            ["messages.request.top_k"] = request.TopK,
            ["messages.request.top_p"] = request.TopP
        };

        var unmapped = new Dictionary<string, JsonElement>();
        foreach (var prop in raw.EnumerateObject())
        {
            metadata[$"messages.request.{prop.Name}"] = prop.Value.Clone();
            if (!MappedRequestFields.Contains(prop.Name))
                unmapped[prop.Name] = prop.Value.Clone();
        }

        metadata["messages.request.unmapped"] = JsonSerializer.SerializeToElement(unmapped, Json);
        return metadata;
    }

    private static AIInputItem ToUnifiedInputItem(MessageParam message, string providerId)
        => new()
        {
            Type = "message",
            Role = message.Role,
            Content = ToUnifiedContentParts(message.Content, providerId).ToList(),
            Metadata = new Dictionary<string, object?>
            {
                ["messages.message.raw"] = JsonSerializer.SerializeToElement(message, Json)
            }
        };

    private static IEnumerable<MessageParam> ToMessageParams(IEnumerable<AIInputItem> items)
    {
        var yielded = new List<MessageParam>();
        var pendingAssistantBlocks = new List<MessageContentBlock>();
        string pendingAssistantRole = "assistant";

        void FlushAssistant()
        {
            if (pendingAssistantBlocks.Count == 0)
                return;

            yielded.Add(new MessageParam
            {
                Role = pendingAssistantRole,
                Content = CreateMessagesContentFromBlocks([.. pendingAssistantBlocks])
            });

            pendingAssistantBlocks.Clear();
        }

        foreach (var item in items)
        {
            pendingAssistantRole = NormalizeRole(item.Role);

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart))
                    {
                        if (assistantBlock is not null)
                        {
                            FlushAssistant();
                            yielded.Add(new MessageParam
                            {
                                Role = "assistant",
                                Content = CreateMessagesContentFromBlocks([assistantBlock])
                            });
                        }

                        if (userBlock is not null)
                        {
                            yielded.Add(new MessageParam
                            {
                                Role = "user",
                                Content = CreateMessagesContentFromBlocks([userBlock])
                            });
                        }
                    }

                    continue;
                }

                var raw = ExtractRawBlock(part.Metadata);
                if (raw is not null)
                {
                    pendingAssistantBlocks.Add(raw);
                    continue;
                }

                switch (part)
                {
                    case AITextContentPart text:
                        pendingAssistantBlocks.Add(new MessageContentBlock { Type = "text", Text = text.Text });
                        break;
                    case AIReasoningContentPart reasoning:
                        pendingAssistantBlocks.Add(new MessageContentBlock { Type = "thinking", Thinking = reasoning.Text });
                        break;
                    case AIFileContentPart file:
                        var fileBlock = ToMessageFileBlock(file);
                        if (fileBlock != null)
                            pendingAssistantBlocks.Add(fileBlock);
                        break;
                }
            }
        }

        FlushAssistant();

        foreach (var message in yielded)
            yield return message;
    }


    public static T? GetProviderOption<T>(
         this MessagesRequestMetadata metadata,
        string providerId,
        string key)
    {
        if (metadata is null)
            return default;

        if (metadata.AdditionalProperties?.TryGetValue(providerId, out var provider) != true)
            return default;

        if (!provider.TryGetProperty(key, out var value))
            return default;

        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return default;

        return value.Deserialize<T>(JsonSerializerOptions.Web);
    }

    public static List<MessageToolDefinition>? GetMessageToolDefinitions(
        this MessagesRequestMetadata metadata,
        string providerId)
    {
        if (metadata is null)
            return null;

        if (metadata.AdditionalProperties?.TryGetValue(providerId, out var providerObj) != true)
            return null;

        if (!providerObj.TryGetProperty("tools", out var toolsEl) ||
            toolsEl.ValueKind != JsonValueKind.Array)
            return null;

        var result = new List<MessageToolDefinition>();

        foreach (var toolEl in toolsEl.EnumerateArray())
        {
            try
            {
                var def = toolEl.Deserialize<MessageToolDefinition>(JsonSerializerOptions.Web);
                if (def != null)
                    result.Add(def);
            }
            catch
            {
                // ignore invalid tool entries (passthrough safety)
            }
        }

        return result.Count > 0 ? result : null;
    }
}
