using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private static List<string>? BuildViduReferenceImages(ImageRequest request)
    {
        if (request.Files?.Any() != true)
            return null;

        return [
            .. request.Files
                .Select(f => f.Data.ToDataUrl(f.MediaType))
                .Where(s => !string.IsNullOrWhiteSpace(s))
        ];
    }

    private static List<Dictionary<string, object?>>? BuildRunwayInputsReferenceImages(ImageRequest request)
    {
        if (request.Files?.Any() != true)
            return null;

        var files = request.Files.ToList();
        var result = new List<Dictionary<string, object?>>(files.Count);

        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            result.Add(new Dictionary<string, object?>
            {
                ["image"] = f.Data.ToDataUrl(f.MediaType),
                ["tag"] = $"img{i + 1}"
            });
        }

        return result;
    }

    private static List<string>? BuildMidjourneyInputsReferenceImages(ImageRequest request)
    {
        if (request.Files?.Any() != true)
            return null;

        return [
            .. request.Files
                .Select(f => f.Data.ToDataUrl(f.MediaType))
                .Where(s => !string.IsNullOrWhiteSpace(s))
        ];
    }

    private static List<string>? BuildInputsReferenceImagesAsStrings(ImageRequest request)
        => BuildMidjourneyInputsReferenceImages(request);

    private static (int width, int height)? TryParseSize(string size)
    {
        if (string.IsNullOrWhiteSpace(size))
            return null;

        var parts = size
            .Replace(":", "x", StringComparison.OrdinalIgnoreCase)
            .Split('x', StringSplitOptions.RemoveEmptyEntries);

        return parts.Length == 2
            && int.TryParse(parts[0], out var w)
            && int.TryParse(parts[1], out var h)
                ? (w, h)
                : null;
    }
}

