using System.Net.Mime;
using AIHappey.Common.Model;

namespace AIHappey.Core.AI;

public static class UIMessagePartExtensions
{
    public static string GuessModelType(
        this string modelId)
    {
        if (modelId.Contains("embed"))
            return "embedding";

        if (modelId.Contains("rerank"))
            return "rerank";

        if (modelId.Contains("image")
            || modelId.Contains("flux")
            || modelId.Contains("imagen")
            || modelId.Contains("dall-e"))
            return "image";

        if (modelId.Contains("openai/sora-") || modelId.Contains("veo-"))
            return "video";

        if (modelId.Contains("tts") || modelId.Contains("realtime"))
            return "audio";

        return "language";
    }

    public static string ToModelId(
        this string modelId, string provider) => $"{provider}/{modelId}";

    public static string GetModelId(
        this ChatRequest chatRequest) => chatRequest.Model.SplitModelId().Model;

    public static (string Provider, string Model) SplitModelId(this string modelId)
    {
        var parts = modelId.Split("/");

        var provider = parts.First();
        var model = string.Join("/", parts.Skip(1));

        return (provider, model);
    }

    public static string ToDataUrl(
        this string data, string mimeType) => $"data:{mimeType};base64,{data}";

    public static FileUIPart ToFileUIPart(this byte[] bytes, string mimeType)
        => new()
        {
            MediaType = mimeType,
            Url = Convert.ToBase64String(bytes).ToDataUrl(mimeType),
        };

    public static IEnumerable<FileUIPart> GetImages(this IEnumerable<UIMessagePart>? parts)
        => parts?.OfType<FileUIPart>()
            .Where(a => a.IsImage()) ?? [];

    public static bool IsImage(this UIMessagePart? part)
        => part is FileUIPart fileUIPart && fileUIPart.MediaType.StartsWith("image/");

    public static IEnumerable<FileUIPart> GetPdfFiles(this IEnumerable<UIMessagePart>? parts)
        => parts?.OfType<FileUIPart>()
            .Where(a => a.MediaType.Equals(MediaTypeNames.Application.Pdf, StringComparison.OrdinalIgnoreCase)) ?? [];

    /// <summary>
    /// Tries to extract image data from a FileUIPart if it is a data:image/*;base64 URI.
    /// Returns null if not valid.
    /// </summary>
    public static BinaryData? TryGetImageData(this FileUIPart filePart)
    {
        if (filePart?.MediaType is null
            || !filePart.MediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            || filePart.Url is null
            || !filePart.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return null;

        const string base64Marker = ";base64,";
        int idx = filePart.Url.IndexOf(base64Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var base64 = filePart.Url[(idx + base64Marker.Length)..];
        try
        {
            var binaryData = Convert.FromBase64String(base64);
            return BinaryData.FromBytes(binaryData);
        }
        catch
        {
            return null;
        }
    }

    public static string? GetRawBase64String(this FileUIPart filePart)
    {
        if (filePart.Url is null
            || !filePart.Url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return filePart?.Url;

        const string base64Marker = ";base64,";
        int idx = filePart.Url.IndexOf(base64Marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return filePart?.Url;

        var base64 = filePart.Url[(idx + base64Marker.Length)..];
        return base64;
    }

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

}
