using AIHappey.Common.Model;

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

    public static string ToDataUrl(this ImageFile imageContentBlock) => imageContentBlock.Data.ToDataUrl(imageContentBlock.MediaType);

    public static int? GetImageWidth(this ImageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Size))
            return null;

        var parts = request.Size.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
            return null;

        return int.TryParse(parts[0], out var width) ? width : null;
    }

    public static int? GetImageHeight(this ImageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Size))
            return null;

        var parts = request.Size.Split('x', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return null;

        return int.TryParse(parts[1], out var width) ? width : null;
    }
}
