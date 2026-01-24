using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplyHyperfluxPayload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "HyperFlux is treated as text-to-image; input images were ignored." });

        // aspect_ratio + seed handled in shared builder.
        var cfg = imageGeneration?.Hyperflux;
        if (cfg?.Styling is not null)
        {
            var styling = new Dictionary<string, object?>();

            if (cfg.Styling.Effects is not null)
            {
                var effects = new Dictionary<string, object?>();
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Color))
                    effects["color"] = cfg.Styling.Effects.Color;
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Lightning))
                    effects["lightning"] = cfg.Styling.Effects.Lightning;
                if (!string.IsNullOrWhiteSpace(cfg.Styling.Effects.Framing))
                    effects["framing"] = cfg.Styling.Effects.Framing;
                if (effects.Count > 0)
                    styling["effects"] = effects;
            }

            if (cfg.Styling.Colors is { Count: > 0 })
            {
                styling["colors"] = cfg.Styling.Colors
                    .Where(c => c is not null && !string.IsNullOrWhiteSpace(c.Color))
                    .Select(c => new Dictionary<string, object?>
                    {
                        ["color"] = c.Color,
                        ["weight"] = c.Weight
                    })
                    .ToList();
            }

            if (styling.Count > 0)
                payload["styling"] = styling;
        }
    }
}

