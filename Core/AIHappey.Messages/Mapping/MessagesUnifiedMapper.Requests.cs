using System.Text.Json;
using System.Text.Json.Nodes;
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
                Items = [.. request.Messages.Select(message => ToUnifiedInputItem(message, providerId))],
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
            Headers = request.Headers,
            Metadata = BuildUnifiedRequestMetadata(request)
        };
    }

    private static readonly string[] UnsupportedSchemaProperties =
    [
        "minimum",
    "maximum"
    ];

    private static void RemoveUnsupportedSchemaProperties(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var property in UnsupportedSchemaProperties)
                obj.Remove(property);

            foreach (var child in obj)
                RemoveUnsupportedSchemaProperties(child.Value);
        }
        else if (node is JsonArray array)
        {
            foreach (var child in array)
                RemoveUnsupportedSchemaProperties(child);
        }
    }

    private static MessagesOutputFormat? ToMessagesOutputFormat(object? responseFormat)
    {
        if (responseFormat is null)
            return null;

        var element = JsonSerializer.SerializeToElement(responseFormat);

        if (!element.TryGetProperty("type", out var type) ||
            type.GetString() != "json_schema" ||
            !element.TryGetProperty("json_schema", out var jsonSchema) ||
            !jsonSchema.TryGetProperty("schema", out var schema))
            return null;

        var node = JsonNode.Parse(schema.GetRawText());

        RemoveUnsupportedSchemaProperties(node);

        return new MessagesOutputFormat
        {
            Type = "json_schema",
            Schema = JsonSerializer.SerializeToElement(node)
        };
    }


    public static MessagesRequest ToMessagesRequest(this AIRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);

        var metadata = request.Metadata ?? [];
        var inputItems = request.Input?.Items ?? [];

        if (inputItems.Count == 0 && !string.IsNullOrEmpty(request.Input?.Text))
            inputItems = [new AIInputItem() {
            Role = "user",
            Content = [new AITextContentPart() {
                Text  =request.Input.Text,
                Type = "text"
            }]
        }];

        var systemFromInput = ToSystemContent(inputItems, providerId);
        var system = systemFromInput
                     ?? (!string.IsNullOrWhiteSpace(request.Instructions) ? new MessagesContent(request.Instructions) : null);

        var metadataObj = JsonSerializer.Deserialize<MessagesRequestMetadata>(
            JsonSerializer.Serialize(metadata, JsonSerializerOptions.Web)
        );

        var providerTools = request.Metadata?
                   .GetMessageToolDefinitions(providerId)
                   ?? [];

        var codeExecutionType = providerTools
            .Select(tool => tool.Type)
            .FirstOrDefault(type =>
                type?.StartsWith(
                    "code_execution_",
                    StringComparison.Ordinal) == true);

        List<MessageToolDefinition>? tools = [.. request.Tools?.Select(a => ToMessageTool(a, codeExecutionType)).ToList() ?? [],
            .. request.Metadata?.GetMessageToolDefinitions(providerId) ?? []];

        var container = request.Metadata?
            .GetProviderOption<JsonElement>(providerId, "container");

        var outputConfig = ExtractObject<MessagesOutputConfig>(metadata, "output_config");
        if (request.ResponseFormat is not null)
        {
            outputConfig ??= new();
            outputConfig.Format = ToMessagesOutputFormat(request.ResponseFormat);
        }

        if (string.IsNullOrEmpty(outputConfig?.Effort)
        && !string.IsNullOrEmpty(request.Verbosity))
        {
            outputConfig?.Effort = request.Verbosity;
        }

        var result = new MessagesRequest
        {
            Model = request.Model,
            Headers = request.Headers,
            MaxTokens = request.MaxOutputTokens ?? request.Metadata?
                .GetProviderOption<int?>(providerId, "max_tokens"),
            Messages = [.. ToMessageParams(inputItems.Where(item => !IsSystemRole(item.Role)), providerId)],
            CacheControl = request.Metadata?
                .GetProviderOption<CacheControlEphemeral>(providerId, "cache_control"),
            Container = container is JsonElement je
                    ? HasSkills(je)
                        ? je.Clone()
                        : ToContainerId(je)
                    : null,
            InferenceGeo = request.Metadata?
                .GetProviderOption<string>(providerId, "inference_geo"),
            Metadata = metadataObj,
            ContextManagement = request.Metadata?
                .GetProviderOption<object>(providerId, "context_management"),
            OutputConfig = outputConfig,
            ServiceTier = request.Metadata?
                .GetProviderOption<string>(providerId, "service_tier"),
            StopSequences = ExtractObject<List<string>>(metadata, "messages.request.stop_sequences"),
            Stream = request.Stream,
            System = system,
            Temperature = request.Temperature,
            Thinking = request.Metadata?
                .GetProviderOption<MessagesThinkingConfig>(providerId, "thinking"),
            ToolChoice = request.ToolChoice == null ? new MessageToolChoice()
            {
                Type = "auto",
                DisableParallelToolUse = false

            } : new MessageToolChoice()
            {
                Type = request.ToolChoice?.ToString()!,
                DisableParallelToolUse = false
            },
            Tools = tools,
            TopK = ExtractValue<int?>(metadata, "messages.request.top_k"),
            TopP = request.TopP,
            AdditionalProperties = ExtractObject<Dictionary<string, JsonElement>>(metadata, "messages.request.unmapped")
        };

        providerId.ApplyProviderOptions(metadata, result.AdditionalProperties ??=
                       [], ["tools", "anthropic-beta", "output_config"]);

        if (result.Container != null)
            result.AdditionalProperties?.Remove("container");

        return result;
    }

    private static bool HasSkills(JsonElement container)
    {
        return container.ValueKind == JsonValueKind.Object
            && container.TryGetProperty("skills", out var skills)
            && skills.ValueKind == JsonValueKind.Array
            && skills.GetArrayLength() > 0;
    }

    private static JsonElement? ToContainerId(JsonElement container)
    {
        if (container.ValueKind == JsonValueKind.String)
            return container;

        if (container.ValueKind == JsonValueKind.Object &&
            container.TryGetProperty("id", out var id) &&
            id.ValueKind == JsonValueKind.String)
        {
            return id.Clone();
        }

        return null;
    }

    private static bool IsSystemRole(string? role)
        => string.Equals(role?.Trim(), "system", StringComparison.OrdinalIgnoreCase);

    private static MessagesContent? ToSystemContent(IEnumerable<AIInputItem> items, string providerId)
    {
        var blocks = new List<MessageContentBlock>();

        foreach (var item in items.Where(item => IsSystemRole(item.Role)))
        {
            foreach (var part in item.Content ?? [])
                AppendMessageBlock(blocks, part, providerId);
        }

        return blocks.Count == 0 ? null : CreateMessagesContentFromBlocks(blocks);
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
            Content = [.. ToUnifiedContentParts(message.Content, providerId)],
            Metadata = new Dictionary<string, object?>
            {
                ["messages.message.raw"] = JsonSerializer.SerializeToElement(message, Json)
            }
        };

    private static IEnumerable<MessageParam> ToMessageParams(IEnumerable<AIInputItem> items, string providerId)
    {
        var yielded = new List<MessageParam>();
        var pendingBlocks = new List<MessageContentBlock>();
        string pendingRole = "assistant";

        void FlushPending()
        {
            if (pendingBlocks.Count == 0)
                return;

            yielded.Add(new MessageParam
            {
                Role = pendingRole,
                Content = CreateMessagesContentFromBlocks([.. pendingBlocks])
            });

            pendingBlocks.Clear();
        }

        foreach (var item in items)
        {
            var itemRole = ResolveMessageRole(item);

            if (pendingBlocks.Count > 0 && !string.Equals(pendingRole, itemRole, StringComparison.Ordinal))
                FlushPending();

            pendingRole = itemRole;

            foreach (var part in item.Content ?? [])
            {
                if (part is AIToolCallContentPart toolPart)
                {
                    foreach (var (assistantBlock, userBlock) in ToMessageToolBlocks(toolPart, providerId))
                    {
                        if (assistantBlock is not null)
                        {
                            if (!string.Equals(pendingRole, "assistant", StringComparison.Ordinal))
                                FlushPending();

                            pendingRole = "assistant";
                            pendingBlocks.Add(assistantBlock);
                        }

                        if (userBlock is not null)
                        {
                            FlushPending();
                            yielded.Add(new MessageParam
                            {
                                Role = "user",
                                Content = CreateMessagesContentFromBlocks([userBlock])
                            });
                        }
                    }

                    continue;
                }

                if (part is not AIFileContentPart || item.Role == "user")
                    AppendMessageBlock(pendingBlocks, part, providerId);
            }
        }

        FlushPending();

        foreach (var message in yielded)
            yield return message;
    }

    private static string ResolveMessageRole(AIInputItem item)
        => IsReasoningOnlyInputItem(item) ? "assistant" : NormalizeRole(item.Role);

    private static bool IsReasoningOnlyInputItem(AIInputItem item)
    {
        if (string.Equals(item.Type?.Trim(), "reasoning", StringComparison.OrdinalIgnoreCase))
            return true;

        return item.Content is { Count: > 0 }
               && item.Content.All(static part => part is AIReasoningContentPart);
    }

    private static void AppendMessageBlock(List<MessageContentBlock> target, AIContentPart part, string providerId)
    {
        var raw = ExtractRawBlock(part.Metadata);
        if (raw is not null)
        {
            target.Add(raw);
            return;
        }

        switch (part)
        {
            case AITextContentPart text:
                target.Add(new MessageContentBlock { Type = "text", Text = text.Text });
                break;
            case AIReasoningContentPart reasoning:
                var signature = reasoning.Signature
                                ?? reasoning.Metadata?.GetProviderOption<string?>(providerId, "signature");

                if (!string.IsNullOrWhiteSpace(signature))
                {
                    target.Add(new MessageContentBlock
                    {
                        Type = "thinking",
                        Thinking = reasoning.Text,
                        Signature = signature
                    });
                }

                break;
            case AIFileContentPart file:
                var fileBlock = ToMessageFileBlock(file);
                if (fileBlock != null)
                    target.Add(fileBlock);
                break;
        }
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
