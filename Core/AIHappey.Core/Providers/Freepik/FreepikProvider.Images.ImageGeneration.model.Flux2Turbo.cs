using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplyFlux2TurboPayload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        // Turbo supports custom image_size object, but to keep it simple we only pass guidance/options.
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Flux 2 Turbo is treated as text-to-image here; input images were ignored." });

        var cfg = imageGeneration?.Flux2Turbo;
        if (cfg?.GuidanceScale is { } gs)
            payload["guidance_scale"] = gs;
        if (cfg?.EnableSafetyChecker is { } sc)
            payload["enable_safety_checker"] = sc;

        // output_format not represented in metadata classes; default upstream.
    }
}

