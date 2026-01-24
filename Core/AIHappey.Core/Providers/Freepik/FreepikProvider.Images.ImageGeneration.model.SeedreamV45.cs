using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplySeedreamV45Payload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Seedream 4.5 is text-to-image; input images were ignored." });

        var cfg = imageGeneration?.SeedreamV45;
        if (cfg?.EnableSafetyChecker is { } sc)
            payload["enable_safety_checker"] = sc;
    }
}

