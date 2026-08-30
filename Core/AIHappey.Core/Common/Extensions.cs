using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.AI;

public static class UIMessagePartExtensions
{
    public static string GuessModelType(this string model)
    {
        var id = model.ToLowerInvariant();

        if (id.Contains("whisper")
            || id.Contains("transcribe")
            || id.Contains("-asr")
            || id.Contains("speech-to-text")
            || (id.Contains("voxtral") && !id.Contains("tts")))
            return "transcription";

        if (id.Contains("tts")
            || id.Contains("text-to-speech")
            || id.Contains("cartesia")
            || id.Contains("orpheus")
            || id.Contains("kokoro")
            || id.Contains("chatterbox"))
            return "speech";

        if (id.Contains("rerank"))
            return "reranking";

        if (id.Contains("embed")
            || id.Contains("bge-m3")
            || id.Contains("bge-multilingual"))
            return "embedding";

        // Video vóór image vanwege termen als image-to-video.
        if (id.Contains("sora")
            || id.Contains("seedance")
            || id.Contains("veo-")
            || id.Contains("wan-")
            || id.Contains("wan2")
            || id.Contains("wan3")
            || id.Contains("kling")
            || id.Contains("pixverse")
            || id.Contains("runway")
            || id.Contains("hailuo")
            || id.Contains("t2v")
            || id.Contains("i2v")
            || id.Contains("r2v")
            || id.Contains("video"))
            return "video";

        if (id.Contains("image")
            || id.Contains("flux")
            || id.Contains("stable-diffusion")
            || id.Contains("sdxl")
            || id.Contains("sd3.5")
            || id.Contains("dall-e")
            || id.Contains("dalle")
            || id.Contains("imagen")
            || id.Contains("ideogram")
            || id.Contains("riverflow")
            || id.Contains("kandinsky")
            || id.Contains("dreamshaper")
            || id.Contains("bria")
            || id.Contains("seedream")
            || id.Contains("recraft")
            || id.Contains("hidream")
            || id.Contains("qwen-image")
            || id.Contains("upscaler")
            || id.Contains("upscale"))
            return "image";

        if (id.Contains("realtime"))
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
            return BinaryData.FromBytes(binaryData, filePart.MediaType);
        }
        catch
        {
            return null;
        }
    }

    public static BinaryData? ToBinaryData(this FileUIPart filePart)
    {
        if (filePart?.MediaType is null
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
            return BinaryData.FromBytes(binaryData, filePart.MediaType);
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


    public static string? TryGetString(this JsonElement root, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var el) || el.ValueKind != JsonValueKind.String)
            return null;

        return el.GetString();
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement root, string propertyName, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }


}
