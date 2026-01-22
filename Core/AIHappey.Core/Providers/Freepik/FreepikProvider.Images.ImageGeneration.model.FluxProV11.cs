using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplyFluxProV11Payload(
        ImageRequest imageRequest,
        AIHappey.Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Flux pro v1.1 is treated as text-to-image; input images were ignored." });

        var cfg = imageGeneration?.FluxProV11;
        if (cfg?.PromptUpsampling is { } up)
            payload["prompt_upsampling"] = up;
        if (cfg?.SafetyTolerance is { } st)
            payload["safety_tolerance"] = st;
        if (!string.IsNullOrWhiteSpace(cfg?.OutputFormat))
            payload["output_format"] = cfg!.OutputFormat;
    }
}

