using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static (string endpointPath, Dictionary<string, object?> payload) BuildMysticPayload(
        string endpointPath,
        string model,
        ImageRequest imageRequest,
        AIHappey.Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        // Mystic exposes models via mystic/<model> where <model> maps to Freepik's 'model' request field.
        var mysticModel = model["mystic/".Length..].Trim();
        if (string.IsNullOrWhiteSpace(mysticModel))
            throw new ArgumentException("Mystic model must be provided as 'mystic/<model>' (e.g., 'mystic/realism').", nameof(imageRequest));

        payload["model"] = mysticModel;

        // Project constraints: do not support reference images for Mystic yet.
        if (imageRequest.Files?.Any() == true)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Mystic reference images are not supported here; files were ignored." });

        var cfg = imageGeneration?.Mystic;
        if (cfg?.Adherence is { } adherence)
            payload["adherence"] = adherence;
        if (cfg?.Hdr is { } hdr)
            payload["hdr"] = hdr;
        if (cfg?.CreativeDetailing is { } cd)
            payload["creative_detailing"] = cd;
        if (!string.IsNullOrWhiteSpace(cfg?.Engine))
            payload["engine"] = cfg!.Engine;
        if (cfg?.FixedGeneration is { } fixedGen)
            payload["fixed_generation"] = fixedGen;

        // NSFW filtering cannot be disabled for standard API usage.
        if (cfg?.FilterNsfw is false)
        {
            warnings.Add(new { type = "compatibility", feature = "filter_nsfw", details = "Freepik Mystic does not allow disabling NSFW filtering; filter_nsfw was forced to true." });
            payload["filter_nsfw"] = true;
        }
        else if (cfg?.FilterNsfw is true)
        {
            payload["filter_nsfw"] = true;
        }

        return (endpointPath, payload);
    }
}

