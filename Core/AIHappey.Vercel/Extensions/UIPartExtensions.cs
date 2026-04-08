using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Extensions;

public static class UIPartExtensions
{
    public static ToolCallDeltaPart ToToolCallDeltaPart(this string delta,
        string toolCallId) => new()
        {
            ToolCallId = toolCallId,
            InputTextDelta = delta,
        };

    public static bool IsImage(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("image/");

    public static bool IsAudio(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("audio/");

    public static FileUIPart ToFileUIPart(this byte[] bytes, string mimeType, Dictionary<string, Dictionary<string, object>?>? metadata = null)
        => new()
        {
            MediaType = mimeType,
            // Url = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}",
            Url = Convert.ToBase64String(bytes),
            ProviderMetadata = metadata
        };

    public static IEnumerable<FileUIPart> GetImages(this IEnumerable<UIMessagePart>? parts)
        => parts?.OfType<FileUIPart>()
            .Where(a => a.IsImage()) ?? [];

    public static FinishUIPart ToFinishUIPart(
       this string finishReason,
       string model,
       int outputTokens,
       int inputTokens,
       int totalTokens,
       float? temperature,
       int? reasoningTokens = null,
       int? cachedInputTokens = null,
       Dictionary<string, object>? extraMetadata = null
   )
    {
        var metadata = new Dictionary<string, object>
        {
         //   { "finishReason", finishReason },
            { "model", model },
            { "timestamp", DateTime.UtcNow },
            { "outputTokens", outputTokens },
            { "inputTokens", inputTokens },
            { "totalTokens", totalTokens },
        };

        if (temperature.HasValue)
        {
            metadata["temperature"] = temperature;
        }

        if (extraMetadata != null)
        {
            foreach (var kv in extraMetadata)
                metadata[kv.Key] = kv.Value;
        }

        if (reasoningTokens != null)
        {
            metadata["reasoningTokens"] = reasoningTokens.Value;
        }

        if (cachedInputTokens != null)
        {
            metadata["cachedInputTokens"] = cachedInputTokens.Value;
        }

        return new()
        {
            MessageMetadata = metadata,
            FinishReason = finishReason
        };
    }

    public static TextStartUIMessageStreamPart ToTextStartUIMessageStreamPart(this string id, Dictionary<string, object>? metadata = null)
        => new()
        {
            Id = id,
            ProviderMetadata = metadata
        };

    public static TextEndUIMessageStreamPart ToTextEndUIMessageStreamPart(this string id)
        => new()
        {
            Id = id
        };

    public static ErrorUIPart ToErrorUIPart(this string error)
        => new()
        {
            ErrorText = error ?? "Something went wrong"
        };

    public static AbortUIPart ToAbortUIPart(this string reason)
   => new()
   {
       Reason = reason
   };

    public static AIInputItem ToUnifiedInputItem(this UIMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new AIInputItem
        {
            Type = "message",
            Role = message.Role.ToString(),
            Id = message.Id,
            Content = message.Parts.Select(ToUnifiedContentPart).Where(a => a is not null).Select(a => a!).ToList(),
            Metadata = message.Metadata?.ToDictionary(kvp => kvp.Key, kvp => (object?)kvp.Value)
        };
    }


    private static AIContentPart? ToUnifiedContentPart(this UIMessagePart part)
    {
        switch (part)
        {
            case TextUIPart text:
                return new AITextContentPart
                {
                    Type = "text",
                    Text = text.Text,
                    Metadata = new Dictionary<string, object?> { ["vercel.type"] = text.Type }
                };

            case FileUIPart file:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = file.MediaType,
                    Filename = file.Filename,
                    Data = file.Url,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["vercel.type"] = file.Type,
                        ["vercel.providerMetadata"] = file.ProviderMetadata
                    }
                };

            case SourceDocumentPart sourceDocument:
                return new AIFileContentPart
                {
                    Type = "file",
                    MediaType = sourceDocument.MediaType,
                    Filename = sourceDocument.Filename,
                    Data = sourceDocument.Title,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["vercel.type"] = sourceDocument.Type,
                        ["vercel.sourceId"] = sourceDocument.SourceId,
                        ["vercel.providerMetadata"] = sourceDocument.ProviderMetadata
                    }
                };

            case ReasoningUIPart reasoning:
                return new AITextContentPart
                {
                    Type = "text",
                    Text = reasoning.Text,
                    Metadata = new Dictionary<string, object?>
                    {
                        ["vercel.type"] = reasoning.Type,
                        ["vercel.id"] = reasoning.Id,
                        ["vercel.providerMetadata"] = reasoning.ProviderMetadata
                    }
                };
        }

        return new AITextContentPart
        {
            Type = "text",
            Text = JsonSerializer.Serialize(part, part.GetType(), JsonSerializerOptions.Web),
            Metadata = new Dictionary<string, object?>
            {
                ["vercel.type"] = part.Type,
                ["vercel.unmapped"] = true
            }
        };
    }

}



