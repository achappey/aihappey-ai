using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class UIMessagePartExtensions
{
    public static string GuessModelType(
        this string model)
    {
        var modelId = model.ToLowerInvariant();
        
        if (modelId.Contains("whisper")
            || modelId.Contains("transcribe")
            || modelId.Contains("voxtral"))
            return "transcription";

        if (modelId.Contains("tts") || modelId.Contains("canopy"))
            return "speech";

        if (modelId.Contains("rerank"))
            return "reranking";

        if (modelId.Contains("embed"))
            return "embedding";

        if (modelId.Contains("image")
            || modelId.Contains("flux")
            || modelId.Contains("imagen")
            || modelId.Contains("dall-e"))
            return "image";

        if (modelId.Contains("openai/sora-") || modelId.Contains("veo-"))
            return "video";

        if (modelId.Contains("realtime"))
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


    public static string GetAudioExtension(this string mimeType)
    {
        return mimeType.ToLowerInvariant() switch
        {
            "audio/mpeg" => ".mp3",
            "audio/mp3" => ".mp3",
            "audio/wav" => ".wav",
            "audio/x-wav" => ".wav",
            "audio/wave" => ".wav",
            "audio/webm" => ".webm",
            "audio/ogg" => ".ogg",
            "audio/opus" => ".opus",
            "audio/aac" => ".aac",
            "audio/flac" => ".flac",
            "audio/mp4" => ".m4a",
            "audio/x-m4a" => ".m4a",
            "audio/3gpp" => ".3gp",
            "audio/3gpp2" => ".3g2",
            _ => throw new NotSupportedException(mimeType)
        };
    }

    public static StringContent NamedField(this string name, string value)
    {
        var c = new StringContent(value ?? string.Empty, Encoding.UTF8);
        c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            // quoting avoids odd parsers; .NET will keep the quotes
            Name = $"\"{name}\""
        };
        return c;
    }

}
