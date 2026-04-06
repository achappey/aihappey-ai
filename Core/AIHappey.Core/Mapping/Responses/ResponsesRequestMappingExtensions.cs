using System.Text;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Vercel.Models;


namespace AIHappey.Core.AI;

public static class ResponsesRequestMappingExtensions
{
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public static ResponseRequest ToResponsesRequest(
        this ChatRequest chatRequest,
        string providerId,
        ResponsesRequestMappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        options ??= new ResponsesRequestMappingOptions();

        var messages = options.NormalizeApprovals
            ? chatRequest.Messages.EnsureApprovals()
            : chatRequest.Messages;

        var tools = options.Tools?.ToList()
            ?? chatRequest.Tools?.Select(tool => (options.ToolDefinitionFactory ?? DefaultToolDefinitionFactory)(tool)).ToList();

        var text = options.Text
            ?? options.TextFactory?.Invoke(chatRequest)
            ?? chatRequest.ToResponsesTextSettings();

        return new ResponseRequest
        {
            Model = options.Model ?? chatRequest.Model,
            Instructions = options.Instructions,
            Input = messages.ToResponsesInput(providerId, options),
            Temperature = chatRequest.Temperature,
            TopP = chatRequest.TopP,
            Reasoning = options.Reasoning,
            MaxOutputTokens = chatRequest.MaxOutputTokens,
            ParallelToolCalls = options.ParallelToolCalls,
            Stream = options.Stream,
            Store = options.Store,
            ServiceTier = options.ServiceTier,
            Include = options.Include?.ToList(),
            Metadata = options.Metadata is null ? null : new Dictionary<string, object?>(options.Metadata),
            ToolChoice = options.ToolChoice ?? chatRequest.ToolChoice,
            Text = text,
            Tools = tools?.Count > 0 ? tools : null,
        };
    }

    public static ResponseInput ToResponsesInput(
        this IEnumerable<UIMessage> messages,
        string providerId,
        ResponsesRequestMappingOptions? options = null)
        => new([.. messages.SelectMany(message => message.ToResponsesInputItems(providerId, options))]);

    public static IEnumerable<ResponseInputItem> ToResponsesInputItems(
        this UIMessage message,
        string providerId,
        ResponsesRequestMappingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        options ??= new ResponsesRequestMappingOptions();

        switch (message.Role)
        {
            case Role.user:
                {
                    var parts = message.Parts.ToResponsesInputContentParts(options?.InputImageDetail
                        ?? "auto").ToList();
                    if (parts.Count > 0)
                    {
                        yield return new ResponseInputMessage
                        {
                            Role = ResponseRole.User,
                            Content = new ResponseMessageContent(parts)
                        };
                    }

                    yield break;
                }

            case Role.system:
                {
                    var parts = message.Parts.ToResponsesInputContentParts().ToList();
                    if (parts.Count > 0)
                    {
                        yield return new ResponseInputMessage
                        {
                            Role = ResponseRole.System,
                            Content = new ResponseMessageContent(parts)
                        };
                    }

                    yield break;
                }

            case Role.assistant:
                {
                    foreach (var part in message.Parts)
                    {
                        switch (part)
                        {
                            case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                                yield return new ResponseInputMessage
                                {
                                    Role = ResponseRole.Assistant,
                                    Phase = message.Metadata.GetProviderProperty<string>(providerId, "phase"),
                                    Content = new ResponseMessageContent(
                                    [
                                        new OutputTextPart
                                        {
                                            Text = text.Text,
                                            Annotations = []
                                        }
                                    ])
                                };
                                break;

                            case ReasoningUIPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                                {
                                    var encryptedContent = reasoning.ProviderMetadata.GetReasoningSignature(
                                        options.ReasoningSignatureProviderId ?? providerId);
                                    var summaries = reasoning.ProviderMetadata
                                       .GetProviderProperty<List<ResponseReasoningSummaryTextPart>>(providerId, "summary");

                                    if (!string.IsNullOrEmpty(encryptedContent))
                                    {
                                        yield return new ResponseReasoningItem
                                        {
                                            EncryptedContent = encryptedContent,
                                            Summary = summaries ?? [new ResponseReasoningSummaryTextPart() {
                                                Text = reasoning.Text
                                            }]
                                        };

                                    }
                                }
                                break;

                            case ToolInvocationPart toolInvocation:
                                {
                                    if (toolInvocation.ProviderExecuted != true)
                                    {
                                        yield return new ResponseFunctionCallItem
                                        {
                                            Id = toolInvocation.ToolCallId,
                                            CallId = toolInvocation.ToolCallId,
                                            Name = toolInvocation.GetToolName(),
                                            Arguments = Serialize(toolInvocation.Input),
                                            Status = "completed"
                                        };

                                        yield return new ResponseFunctionCallOutputItem
                                        {
                                            CallId = toolInvocation.ToolCallId,
                                            Output = toolInvocation.ExcludeMeta(),
                                            Status = "completed"
                                        };
                                    }

                                    break;
                                }
                        }
                    }

                    yield break;
                }

            default:
                throw new NotSupportedException($"Unsupported UI message role: {message.Role}");
        }
    }

    public static IEnumerable<ResponseContentPart> ToResponsesInputContentParts(this IEnumerable<UIMessagePart> parts, string imageInputDetail = "auto")
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                    yield return new InputTextPart(text.Text);
                    break;

                case FileUIPart file:
                    yield return file.ToResponsesInputContentPart(imageInputDetail);
                    break;
            }
        }
    }

    public static ResponseContentPart ToResponsesInputContentPart(this FileUIPart file, string imageInputDetail = "auto")
    {
        ArgumentNullException.ThrowIfNull(file);

        if (file.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return new InputImagePart
            {
                ImageUrl = file.Url,
                Detail = imageInputDetail
            };
        }

        if (file.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            var comma = file.Url.IndexOf(',');
            if (comma > -1)
            {
                var metadata = file.Url[5..comma];
                var payload = file.Url[(comma + 1)..];

                if (file.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                {
                    return new InputTextPart(DecodeDataUrlPayload(metadata, payload));
                }
            }

            return new InputFilePart
            {
                FileData = file.Url,
                Filename = file.Filename
            };
        }

        return new InputFilePart
        {
            FileUrl = file.Url,
            Filename = file.Filename
        };
    }

    public static object? ToResponsesTextSettings(this ChatRequest chatRequest)
    {
        var schema = chatRequest.ResponseFormat?.GetJSONSchema();

        if (schema?.JsonSchema is not null)
        {
            return new
            {
                format = new
                {
                    type = "json_schema",
                    name = schema.JsonSchema.Name,
                    schema = schema.JsonSchema.Schema,
                    description = schema.JsonSchema.Description,
                    strict = schema.JsonSchema.Strict
                }
            };
        }

        if (chatRequest.ResponseFormat != null)
        {
            return new
            {
                format = new
                {
                    type = "json_object"
                }
            };
        }

        return null;
    }

    public static ResponseToolDefinition ToTool(this JsonElement element)
    {
        var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(element)!;

        return new ResponseToolDefinition
        {
            Type = dict.TryGetValue("type", out var t) ? t.GetString()! : "",
            Extra = dict
        };
    }

    public static ResponseToolDefinition ToResponseToolDefinition(this Tool tool)
        => DefaultToolDefinitionFactory(tool);

    private static ResponseToolDefinition DefaultToolDefinitionFactory(Tool tool)
    {
        var extra = new Dictionary<string, JsonElement>
        {
            ["name"] = JsonSerializer.SerializeToElement(tool.Name, Json)
        };

        if (!string.IsNullOrWhiteSpace(tool.Description))
            extra["description"] = JsonSerializer.SerializeToElement(tool.Description, Json);

        if (tool.InputSchema != null)
            extra["parameters"] = JsonSerializer.SerializeToElement(tool.InputSchema, Json);

        return new ResponseToolDefinition
        {
            Type = "function",
            Extra = extra
        };
    }

    private static string Serialize(object? value)
        => value is null
            ? "{}"
            : value is string text
                ? text
                : JsonSerializer.Serialize(value, Json);

    private static string DecodeDataUrlPayload(string metadata, string payload)
    {
        if (metadata.Contains(";base64", StringComparison.OrdinalIgnoreCase))
            return Encoding.UTF8.GetString(Convert.FromBase64String(payload));

        return Uri.UnescapeDataString(payload);
    }
}

