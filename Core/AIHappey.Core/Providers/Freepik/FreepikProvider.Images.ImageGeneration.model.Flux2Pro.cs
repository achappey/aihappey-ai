using AIHappey.Common.Extensions;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider
{
    private static void ApplyFlux2ProPayload(
        ImageRequest imageRequest,
        AIHappey.Common.Model.Providers.Freepik.ImageGeneration.ImageGeneration? imageGeneration,
        Dictionary<string, object?> payload,
        List<object> warnings)
    {
        // Defaults per Freepik docs
        payload["width"] = imageRequest.GetImageWidth() ?? 1024;
        payload["height"] = imageRequest.GetImageHeight() ?? 768;

        // Optional image-to-image / multi-image. Freepik expects base64 in input_image(_2.._4).
        var files = imageRequest.Files?.ToList() ?? [];
        if (files.Count > 0)
        {
            if (files.Count > 4)
                warnings.Add(new { type = "unsupported", feature = "files", details = "Flux 2 Pro supports up to 4 input images; extra images were ignored." });

            for (var i = 0; i < Math.Min(files.Count, 4); i++)
            {
                var f = files[i];
                EnsureIsRawBase64Only(f?.Data, $"files[{i}].data");
                payload[i switch
                {
                    0 => "input_image",
                    1 => "input_image_2",
                    2 => "input_image_3",
                    3 => "input_image_4",
                    _ => throw new InvalidOperationException()
                }] = f!.Data;
            }
        }

        var cfg = imageGeneration?.Flux2;
        if (cfg?.PromptUpsampling is { } up)
            payload["prompt_upsampling"] = up;
    }
}

