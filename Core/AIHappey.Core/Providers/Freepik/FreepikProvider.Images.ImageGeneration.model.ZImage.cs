using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplyZImagePayload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Z-Image is text-to-image; input images were ignored." });

        // Prefer new per-model metadata (z-image-turbo), fallback to legacy (zimageturbo) for backward compatibility.
        var cfgNew = imageGeneration?.ZImageTurboModel;
        if (cfgNew is not null)
        {
            if (!string.IsNullOrWhiteSpace(cfgNew.ImageSize))
                payload["image_size"] = cfgNew.ImageSize;
            if (cfgNew.NumInferenceSteps is { } steps)
                payload["num_inference_steps"] = steps;
            if (!string.IsNullOrWhiteSpace(cfgNew.OutputFormat))
                payload["output_format"] = cfgNew.OutputFormat;
            if (cfgNew.EnableSafetyChecker is { } sc)
                payload["enable_safety_checker"] = sc;
            return;
        }

        var cfgLegacy = imageGeneration?.ZImageTurbo;
        if (cfgLegacy?.NumInferenceSteps is { } stepsLegacy)
            payload["num_inference_steps"] = stepsLegacy;
        if (cfgLegacy?.EnableSafetyChecker is { } scLegacy)
            payload["enable_safety_checker"] = scLegacy;
    }
}

