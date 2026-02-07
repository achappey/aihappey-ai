using AIHappey.Vercel.Models;

namespace AIHappey.Common.Extensions;

public static class ImageExtensions
{
    public static (int width, int height)? InferSizeFromAspectRatio(
            this string aspectRatio,
            int minWidth = 1024,
            int maxWidth = 1920,
            int minHeight = 1024,
            int maxHeight = 1080)
    {
        if (string.IsNullOrWhiteSpace(aspectRatio))
            return null;

        var parts = aspectRatio.Split(':');
        if (parts.Length != 2)
            return null;

        if (!double.TryParse(parts[0], out var arW) ||
            !double.TryParse(parts[1], out var arH) ||
            arW <= 0 || arH <= 0)
            return null;

        var ratio = arW / arH;

        // Try max width first
        var width = maxWidth;
        var height = (int)Math.Round(width / ratio);

        if (height < minHeight || height > maxHeight)
        {
            // Try max height instead
            height = maxHeight;
            width = (int)Math.Round(height * ratio);

            if (width < minWidth || width > maxWidth)
                return null; // cannot satisfy within bounds
        }

        return (width, height);
    }

    public static string ToDataUrl(
        this string data, string mimeType) => $"data:{mimeType};base64,{data}";

    public static string ToDataUrl(
        this BinaryData data, string mimeType) => $"data:{mimeType};base64,{Convert.ToBase64String(data)}";

    public static ImageFile ToImageFile(
        this FileUIPart data) => new()
        {
            Data = data.Url.RemoveDataUrlPrefix(),
            MediaType = data.MediaType
        };


    public static VideoFile ToVideoFile(
        this FileUIPart data) => new()
        {
            Data = data.Url.RemoveDataUrlPrefix(),
            MediaType = data.MediaType
        };


    public static string ToDataUrl(this ImageFile imageContentBlock) => imageContentBlock.Data.ToDataUrl(imageContentBlock.MediaType);

    public static string RemoveDataUrlPrefix(this string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return input;

        var commaIndex = input.IndexOf(',');

        // not a data URL → return as-is
        if (!input.StartsWith("data:", StringComparison.OrdinalIgnoreCase) || commaIndex < 0)
            return input;

        return input[(commaIndex + 1)..];
    }


}
