using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Extensions;

public static class UIPartExtensions
{
    public static bool IsImage(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("image/");

    public static bool IsAudio(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("audio/");

    public static FileUIPart ToFileUIPart(this byte[] bytes, string mimeType)
        => new()
        {
            MediaType = mimeType,
            Url = $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}"
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

    public static TextStartUIMessageStreamPart ToTextStartUIMessageStreamPart(this string id)
        => new()
        {
            Id = id
        };

    public static TextEndUIMessageStreamPart ToTextEndUIMessageStreamPart(this string id)
        => new()
        {
            Id = id
        };

    public static ErrorUIPart ToErrorUIPart(this string error)
        => new()
        {
            ErrorText = error
        };

    public static AbortUIPart ToAbortUIPart(this string reason)
   => new()
   {
       Reason = reason
   };

}



