using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplySeedreamV4EditPayload(
        ImageRequest imageRequest,
        Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        // Docs say reference_images are optional, but per product decision we require >= 1.
        var files = imageRequest.Files?.ToList() ?? [];
        if (files.Count == 0)
            throw new ArgumentException("Seedream v4 Edit requires at least 1 reference image provided in 'files'.", nameof(imageRequest));
        if (files.Count > 5)
            warnings.Add(new { type = "unsupported", feature = "files", details = "Seedream v4 Edit supports up to 5 reference images; extra images were ignored." });

        var refs = new List<string>();
        for (var i = 0; i < Math.Min(files.Count, 5); i++)
        {
            var f = files[i];
            EnsureIsRawBase64Only(f?.Data, $"files[{i}].data");
            refs.Add(f!.Data);
        }

        payload["reference_images"] = refs;
    }
}

