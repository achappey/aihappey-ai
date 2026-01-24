using OpenAI.Responses;
using OAIC = OpenAI.Chat;
using AIHappey.Common.Model;
using System.ClientModel.Primitives;
using AIHappey.Core.AI;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public static class UIMessageExtensions
{
    public static ResponseTextOptions? ToTextOptions(this ChatRequest chatRequest)
    {
        if (chatRequest.ResponseFormat == null) return null;

        var schema = chatRequest.ResponseFormat?.GetJSONSchema();
        var format = schema != null
            ?
            ResponseTextFormat.CreateJsonSchemaFormat(
                schema.JsonSchema.Name,
                BinaryData.FromString(schema.JsonSchema.Schema.GetRawText()),
                schema.JsonSchema.Description,
                schema.JsonSchema.Strict)
            :
            chatRequest.ResponseFormat != null
            ?
            ResponseTextFormat.CreateJsonObjectFormat()
            : ResponseTextFormat.CreateTextFormat();

        return new()
        {
            TextFormat = format
        };
    }

    public static CreateResponseOptions ToResponseCreationOptions(this ChatRequest chatRequest,
        IEnumerable<string>? codeInterpreterFiles = null, string? currentUserId = null)
    {
        var metadata = chatRequest.GetProviderMetadata<OpenAiProviderMetadata>(Constants.OpenAI);


        var options = new CreateResponseOptions
        {
            TruncationMode = ResponseTruncationMode.Auto,
            Temperature = chatRequest.Temperature,
            StoredOutputEnabled = false,
            StreamingEnabled = true,
            MaxOutputTokenCount = chatRequest.MaxOutputTokens,
            MaxToolCallCount = chatRequest.MaxToolCalls,
            TextOptions = chatRequest.ToTextOptions(),
            Instructions = metadata?.Instructions,
            ParallelToolCallsEnabled = metadata?.ParallelToolCalls,
            ToolChoice = chatRequest.ToolChoice?.ToResponseToolChoice(),
            ReasoningOptions = null
        };

        foreach (var include in metadata?.Include ?? [])
        {
            options.IncludedProperties.Add(new IncludedResponseProperty(include));
        }

        if (!string.IsNullOrEmpty(currentUserId))
            options.EndUserId = currentUserId;

        if (metadata?.MCPTools?.Any() != true)
            foreach (var tool in chatRequest.Tools ?? [])
            {
                options.Tools.Add(ResponseTool.CreateFunctionTool(
                    tool.Name,
                    functionDescription: tool.Description,
                    strictModeEnabled: false,
                    functionParameters: BinaryData.FromObjectAsJson(tool.InputSchema)));
            }

        // Add reasoning options from provider metadata
        if (metadata?.Reasoning != null)
        {
            options.ReasoningOptions = new ResponseReasoningOptions
            {
                ReasoningEffortLevel = metadata.Reasoning.Effort,
                ReasoningSummaryVerbosity = metadata.Reasoning.Summary
            };
        }

        // Add web search tool from provider metadata if present
        if (metadata?.WebSearch != null)
        {
            var location = metadata.WebSearch?.UserLocation;

            options.Tools.Add(CreateCustomTool("web_search", new Dictionary<string, object?>()
            {
                { "search_context_size",  metadata.WebSearch?.SearchContextSize },
                { "user_location",  location != null
                    ? new OpenAiUserLocation() {
                        Country = location.Country,
                        City = location.City,
                        Region = location.Region,
                        Timezone = location.Timezone
                    }
                    : null }
             }));
        }

        if (metadata?.FileSearch != null)
        {
            var vectorstoreIds = metadata?.FileSearch.VectorStoreIds;

            if (vectorstoreIds?.Count > 0)
            {
                options.Tools.Add(ResponseTool.CreateFileSearchTool(metadata?.FileSearch.VectorStoreIds,
                    maxResultCount: metadata?.FileSearch.MaxNumResults));
            }
        }

        if (metadata?.CodeInterpreter != null)
        {
            options.Tools.Add(CreateCustomTool(Constants.CodeInterpreter, new Dictionary<string, object?>()
            {
                { "container",
                    metadata.CodeInterpreter.Container.HasValue
                    && metadata.CodeInterpreter.Container.Value.IsString ?
                    metadata.CodeInterpreter.Container.Value.String
                        : new { type = "auto",
                            file_ids = codeInterpreterFiles ?? []
                        } }
             }));
        }

        if (metadata?.ImageGeneration != null)
        {
            options.Tools.Add(CreateCustomTool("image_generation", new Dictionary<string, object?>()
            {
                { "model", metadata?.ImageGeneration.Model },
                { "partial_images", metadata?.ImageGeneration.PartialImages ?? 0 },
                { "quality", metadata?.ImageGeneration.Quality ?? "auto" },
                { "background", metadata?.ImageGeneration.Background ?? "auto" },
                { "input_fidelity", metadata?.ImageGeneration.InputFidelity ?? "low" },
                { "size", metadata?.ImageGeneration.Size ?? "auto" },
             }));
        }

        foreach (var tool in metadata?.MCPTools ?? [])
        {
            options.Tools.Add(CreateCustomTool(
                "mcp",
                new Dictionary<string, object?>() { { "server_label", tool.ServerLabel},
                { tool.ServerUrl != null ? "server_url" : "connector_id", tool.ServerUrl ?? tool.ConnectorId},
                { "require_approval", tool.RequireApproval},
                { "allowed_tools", new { tool_names = tool.AllowedTools}},
                { "authorization", tool.Authorization} }));
        }

        return options;
    }

    // 1.  Helper: convert only the TEXT assistant parts --------------------------
    public static IEnumerable<ResponseContentPart> ToAssistantTextContentParts2(
        this IEnumerable<TextUIPart> textParts, IEnumerable<ResponseMessageAnnotation> annotations)
    {
        foreach (var t in textParts)
            yield return ResponseContentPart.CreateOutputTextPart(t.Text, annotations);
    }

    public static IEnumerable<ResponseContentPart> ToAssistantTextContentParts(
    this IEnumerable<TextUIPart> textParts,
    IEnumerable<ResponseMessageAnnotation> annotations)
    {
        if (textParts == null) yield break;

        // Materialise once so we can know the last element
        var parts = textParts.ToList();
        int lastIndex = parts.Count - 1;

        for (int i = 0; i < parts.Count; i++)
        {
            var t = parts[i];

            // Only attach annotations to the last text part
            var annos = (i == lastIndex) ? annotations : Enumerable.Empty<ResponseMessageAnnotation>();

            yield return ResponseContentPart.CreateOutputTextPart(t.Text, annos);
        }
    }


    public static IEnumerable<OAIC.ChatMessageContentPart> ToAssistantTextChatContentParts(
       this IEnumerable<TextUIPart> textParts)
    {
        foreach (var t in textParts)
            yield return OAIC.ChatMessageContentPart.CreateTextPart(t.Text);
    }


    public static IEnumerable<ResponseMessageAnnotation> MapBackToAnnotations(IEnumerable<SourceUIPart> parts)
    {
        foreach (var part in parts)
        {
            if (part.ProviderMetadata == null ||
                !part.ProviderMetadata.TryGetValue("Type", out var typeObj) ||
                typeObj is not string type)
            {
                continue;
            }

            switch (type)
            {
                case nameof(UriCitationMessageAnnotation):
                    yield return new UriCitationMessageAnnotation
                    (
                        new Uri(part.Url!),
                        Convert.ToInt32(part.ProviderMetadata["StartIndex"]),
                        Convert.ToInt32(part.ProviderMetadata["EndIndex"]),
                        part.Title
                    );
                    break;

                case nameof(FileCitationMessageAnnotation):
                    yield return new FileCitationMessageAnnotation
                    (
                        part.ProviderMetadata["FileId"]?.ToString()!,
                        Convert.ToInt32(part.ProviderMetadata["Index"]),
                        part.ProviderMetadata["Filename"]?.ToString()!
                    );
                    break;

                case nameof(FilePathMessageAnnotation):
                    yield return new FilePathMessageAnnotation
                    (
                        part.ProviderMetadata["FileId"]?.ToString()!,
                        Convert.ToInt32(part.ProviderMetadata["Index"])
                    );
                    break;
            }
        }
    }


    // 2.  New top-level extension -------------------------------------------------
    /// <summary>
    /// Converts a <see cref="UIMessage"/> into one-or-more <see cref="ResponseItem"/>s.
    /// • user  → 1 item (same as before)  
    /// • assistant/text-only → 1 item (same as before)  
    /// • assistant/with tool invocation →
    ///     a)  0 – 1 text item (if there is free text)  
    ///     b)  1 function-call item  
    ///     c)  1 function-call-output item
    /// </summary>
    public static IEnumerable<ResponseItem> ToResponseItems(this UIMessage message)
    {
        switch (message.Role)
        {
            //----------------------------------------------------------------------
            // USER → exactly one response item
            //----------------------------------------------------------------------
            case Role.user:
                var parts = message.Parts.ToInputMessageResponseContentParts();
                if (parts.Any())
                    yield return ResponseItem.CreateUserMessageItem(
                        parts);

                yield break;
            case Role.system:
                yield return ResponseItem.CreateSystemMessageItem(
                    message.Parts.ToInputMessageResponseContentParts());
                yield break;

            //----------------------------------------------------------------------
            // ASSISTANT
            //----------------------------------------------------------------------
            case Role.assistant:
                foreach (var part in message.Parts)
                {
                    switch (part)
                    {
                        case TextUIPart text:
                            yield return ResponseItem.CreateAssistantMessageItem(
                                [.. new[] { text }.ToAssistantTextContentParts([])]);
                            break;

                        case ReasoningUIPart reasoning:
                            var signature = reasoning.ProviderMetadata.GetReasoningSignature(Constants.OpenAI);

                            yield return !string.IsNullOrEmpty(signature)
                                ? new ReasoningResponseItem(reasoning.Text)
                                {
                                    EncryptedContent = signature
                                }
                                : ResponseItem.CreateReasoningItem(reasoning.Text);
                            break;

                        case ToolInvocationPart tip:
                            yield return ResponseItem.CreateFunctionCallItem(
                                tip.ToolCallId,
                                tip.GetToolName(),
                                BinaryData.FromObjectAsJson(tip.Input));

                            yield return ResponseItem.CreateFunctionCallOutputItem(
                               tip.ToolCallId,
                               tip.ExcludeMeta());

                            break;
                        default:
                            break;
                            // ... other part types
                    }
                }

                yield break;
            default:
                throw new NotSupportedException(
                    $"Unsupported UIMessage role: {message.Role}");
        }
    }

    public static IEnumerable<OAIC.ChatMessage> ToChatMessages(this UIMessage message)
    {
        switch (message.Role)
        {
            //----------------------------------------------------------------------
            // USER → exactly one response item
            //----------------------------------------------------------------------
            case Role.user:
                yield return OAIC.ChatMessage.CreateUserMessage(
                    message.Parts.ToInputMessageChatContentParts());
                yield break;
            case Role.system:
                yield return OAIC.ChatMessage.CreateSystemMessage(
                    message.Parts.ToInputMessageChatContentParts());
                yield break;

            //----------------------------------------------------------------------
            // ASSISTANT
            //----------------------------------------------------------------------
            case Role.assistant:
                // (1) Free-form assistant text, if any
                var textParts = message.Parts.OfType<TextUIPart>().ToList();
                if (textParts.Count > 0)
                {
                    yield return OAIC.ChatMessage.CreateAssistantMessage(
                        textParts.ToAssistantTextChatContentParts().ToList());
                }

                yield break;

            default:
                throw new NotSupportedException(
                    $"Unsupported UIMessage role: {message.Role}");
        }
    }

    public static ResponseTool CreateCustomTool(this string type, IDictionary<string, object?>? extra = null)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = type
        };

        if (extra != null)
            foreach (var kv in extra) dict[kv.Key] = kv.Value;

        // Important: use "J" (JSON) format so the SDK uses its IJsonModel path.
        return ModelReaderWriter.Read<ResponseTool>(
            BinaryData.FromObjectAsJson(dict),
            new ModelReaderWriterOptions("J")
        )!;
    }
}
