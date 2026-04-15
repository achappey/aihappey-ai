using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Extensions;

public static class UIPartExtensions
{
    public static bool IsImage(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("image/");

    public static bool IsAudio(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("audio/");

    public static FileUIPart ToFileUIPart(this byte[] bytes, string mimeType, Dictionary<string, Dictionary<string, object>?>? metadata = null)
        => new()
        {
            MediaType = mimeType,
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
        FinishGatewayMetadata? gateway = null;
        long? runtimeMs = null;
        Dictionary<string, object?>? additionalProperties = null;

        if (extraMetadata is not null)
        {
            additionalProperties = [];
            foreach (var (key, value) in extraMetadata)
            {
                switch (key)
                {
                    case "gateway" when value is Dictionary<string, object> gatewayDict:
                        gateway = FinishGatewayMetadata.FromDictionary(gatewayDict);
                        break;
                    case "runtimeMs":
                        runtimeMs = value switch
                        {
                            long l => l,
                            int i => i,
                            string s when long.TryParse(s, out var parsed) => parsed,
                            _ => runtimeMs
                        };
                        break;
                    default:
                        additionalProperties[key] = value;
                        break;
                }
            }

            if (additionalProperties.Count == 0)
                additionalProperties = null;
        }

        return new()
        {
            MessageMetadata = FinishMessageMetadata.Create(
                model: model,
                timestamp: DateTimeOffset.UtcNow,
                outputTokens: outputTokens,
                inputTokens: inputTokens,
                totalTokens: totalTokens,
                temperature: temperature,
                reasoningTokens: reasoningTokens,
                cachedInputTokens: cachedInputTokens,
                runtimeMs: runtimeMs,
                gateway: gateway,
                additionalProperties: additionalProperties),
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


}



