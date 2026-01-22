using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplySeedreamPayload(
        ImageRequest imageRequest,
        AIHappey.Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Seedream is text-to-image; input images were ignored." });

        var cfg = imageGeneration?.Seedream;
        if (cfg?.GuidanceScale is { } gs)
            payload["guidance_scale"] = gs;
    }
}

