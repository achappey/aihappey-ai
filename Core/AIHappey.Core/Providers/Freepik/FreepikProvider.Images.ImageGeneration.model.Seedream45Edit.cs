using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplySeedream45EditPayload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        // Requires 1-14 reference images. Freepik supports URLs OR base64.
        // Project rule: only support base64 incoming; do not download URLs.
        var files = imageRequest.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Seedream 4.5 Edit requires 1-14 reference images provided in 'files'.", nameof(imageRequest));
        if (files.Count > 14)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Seedream 4.5 Edit supports up to 14 reference images; extra images were ignored." });

        var refs = new List<string>();
        for (var i = 0; i < Math.Min(files.Count, 14); i++)
        {
            var f = files[i];
            EnsureIsRawBase64Only(f?.Data, $"files[{i}].data");
            refs.Add(f!.Data);
        }
        payload["reference_images"] = refs;

        var cfg = imageGeneration?.Seedream45Edit;
        if (cfg?.EnableSafetyChecker is { } sc)
            payload["enable_safety_checker"] = sc;
    }
}

