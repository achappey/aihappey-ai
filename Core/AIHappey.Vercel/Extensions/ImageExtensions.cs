using AIHappey.Vercel.Models;

namespace AIHappey.Vercel.Extensions;

public static class ImageExtensions
{

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



