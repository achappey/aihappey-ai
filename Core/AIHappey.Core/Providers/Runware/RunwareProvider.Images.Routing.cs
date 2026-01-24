using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.Runware;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Runware;

public sealed partial class RunwareProvider
{
    private const string ViduQ1ImageModelId = "vidu:q1@image";
    private const string KlingImageO1ModelId = "klingai:kling-image@o1";
    private const string PrunaAiImageEditModelId = "prunaai:2@1";
    private const string Ideogram30ModelId = "ideogram:4@1";
    private const string Ideogram30RemixModelId = "ideogram:4@2";

    private const string BytedanceSeedEdit30ModelId = "bytedance:4@1";
    private const string BytedanceSeedream40ModelId = "bytedance:5@0";
    private const string BytedanceSeedream45ModelId = "bytedance:seedream@4.5";

    private const string Bria32ModelId = "bria:10@1";
    private const string BriaFiboModelId = "bria:20@1";

    private const string SourcefulRiverflow11MiniModelId = "sourceful:1@0";
    private const string SourcefulRiverflow11ModelId = "sourceful:1@1";
    private const string SourcefulRiverflow11ProModelId = "sourceful:1@2";
    private const string SourcefulRiverflow2PreviewStandardModelId = "sourceful:2@1";
    private const string SourcefulRiverflow2PreviewFastModelId = "sourceful:2@2";
    private const string SourcefulRiverflow2PreviewMaxModelId = "sourceful:2@3";

    private const string BflFlux11ProModelId = "bfl:2@1";
    private const string BflFlux11ProUltraModelId = "bfl:2@2";
    private const string BflFlux1FillProModelId = "bfl:1@2";
    private const string BflFlux1ExpandProModelId = "bfl:1@3";
    private const string BflFlux2DevModelId = "runware:400@1";

    private static void AddImageInputs(Dictionary<string, object?> payload, ImageRequest request, RunwareImageProviderMetadata? options)
    {
        var model = request.Model;

        if (model.StartsWith("bfl:", StringComparison.OrdinalIgnoreCase)
            || model.Equals(BflFlux2DevModelId, StringComparison.OrdinalIgnoreCase))
        {
            AddBflInputs(payload, request, options);
        }
        else if (model.Equals(BriaFiboModelId, StringComparison.OrdinalIgnoreCase))
        {
            // Bria FIBO supports image-to-image via inputs.image.
            // Minimal checks: if a file is present, map files[0] to inputs.image.
            if (request.Files?.Any() == true)
            {
                var first = request.Files.First();
                var inputs = payload.TryGetValue("inputs", out var inputsObj) && inputsObj is Dictionary<string, object?> existing
                    ? existing
                    : new Dictionary<string, object?>();

                inputs["image"] = first.Data.ToDataUrl(first.MediaType);
                payload["inputs"] = inputs;
            }
        }
        else if (model.Equals(Bria32ModelId, StringComparison.OrdinalIgnoreCase))
        {
            // Bria 3.2 is text-to-image.
            // Do NOT map uploaded files to seedImage; Bria image-conditioning should be provided via controlnet/ipAdapter pass-through.
        }
        else if (model.Equals(ViduQ1ImageModelId, StringComparison.OrdinalIgnoreCase))
        {
            if (BuildViduReferenceImages(request) is { Count: > 0 } referenceImages)
                payload["referenceImages"] = referenceImages;
        }
        else if (model.Equals(Ideogram30RemixModelId, StringComparison.OrdinalIgnoreCase))
        {
            // Ideogram Remix is image-to-image and expects exactly 1 reference image.
            if (request.Files?.Any() == true)
            {
                var first = request.Files.First();
                payload["referenceImages"] = new List<string>(1) { first.Data.ToDataUrl(first.MediaType) };
            }
        }
        else if (model.StartsWith("runway:", StringComparison.OrdinalIgnoreCase))
        {
            if (BuildRunwayInputsReferenceImages(request) is { Count: > 0 } runwayReferenceImages)
            {
                payload["inputs"] = new Dictionary<string, object?>
                {
                    ["referenceImages"] = runwayReferenceImages
                };
            }
        }
        else if (model.StartsWith("midjourney:", StringComparison.OrdinalIgnoreCase))
        {
            if (BuildMidjourneyInputsReferenceImages(request) is { Count: > 0 } midjourneyReferenceImages)
            {
                payload["inputs"] = new Dictionary<string, object?>
                {
                    ["referenceImages"] = midjourneyReferenceImages
                };
            }
        }
        else if (model.Equals(KlingImageO1ModelId, StringComparison.OrdinalIgnoreCase)
            || model.Equals(PrunaAiImageEditModelId, StringComparison.OrdinalIgnoreCase))
        {
            if (BuildInputsReferenceImagesAsStrings(request) is { Count: > 0 } referenceImages)
            {
                payload["inputs"] = new Dictionary<string, object?>
                {
                    ["referenceImages"] = referenceImages
                };
            }
        }
        else if (model.Equals(BytedanceSeedEdit30ModelId, StringComparison.OrdinalIgnoreCase)
            || model.Equals(BytedanceSeedream40ModelId, StringComparison.OrdinalIgnoreCase))
        {
            // ByteDance SeedEdit / Seedream expect reference images at the top-level property: referenceImages
            // SeedEdit requires exactly 1 reference image (Runware accepts either UUIDs or data URLs).
            if (BuildInputsReferenceImagesAsStrings(request) is { Count: > 0 } referenceImages)
            {
                if (model.Equals(BytedanceSeedEdit30ModelId, StringComparison.OrdinalIgnoreCase) && referenceImages.Count != 1)
                    throw new ArgumentException("ByteDance SeedEdit 3.0 requires exactly 1 reference image.", nameof(request));

                var maxSequential = options?.ProviderSettings?.Bytedance?.MaxSequentialImages;
                if (maxSequential is > 0 && referenceImages.Count + maxSequential.Value > 15)
                    throw new ArgumentException("ByteDance referenceImages + maxSequentialImages cannot exceed 15.", nameof(request));

                payload["referenceImages"] = referenceImages;
            }
        }
        else if (model.Equals(BytedanceSeedream45ModelId, StringComparison.OrdinalIgnoreCase))
        {
            // ByteDance Seedream 4.5 expects reference images under inputs.referenceImages.
            if (BuildInputsReferenceImagesAsStrings(request) is { Count: > 0 } referenceImages)
            {
                var maxSequential = options?.ProviderSettings?.Bytedance?.MaxSequentialImages;
                if (maxSequential is > 0 && referenceImages.Count + maxSequential.Value > 15)
                    throw new ArgumentException("ByteDance referenceImages + maxSequentialImages cannot exceed 15.", nameof(request));

                payload["inputs"] = new Dictionary<string, object?>
                {
                    ["referenceImages"] = referenceImages
                };
            }
        }
        else if (model.StartsWith("sourceful:", StringComparison.OrdinalIgnoreCase))
        {
            // Sourceful Riverflow models:
            // - Riverflow 1.* uses inputs.references (image-to-image editing)
            // - Riverflow 2.* uses inputs.referenceImages (optional for text-to-image; required for editing)
            // Docs mention UUIDs; we also allow data URLs (consistent with other Runware models).
            if (BuildInputsReferenceImagesAsStrings(request) is { Count: > 0 } referenceImages)
            {
                var key = model.StartsWith("sourceful:1@", StringComparison.OrdinalIgnoreCase) ? "references" : "referenceImages";
                payload["inputs"] = new Dictionary<string, object?>
                {
                    [key] = referenceImages
                };
            }
        }
        else
        {
            // Ideogram 3.0 (text-to-image) should not get a seedImage from uploaded files.
            if (!model.Equals(Ideogram30ModelId, StringComparison.OrdinalIgnoreCase)
                && request.Files?.Any() == true)
            {
                var first = request.Files.First();
                payload["seedImage"] = first.Data.ToDataUrl(first.MediaType);
            }
        }

        if (request.Mask is not null)
            payload["maskImage"] = request.Mask.Data.ToDataUrl(request.Mask.MediaType);
    }

    private static void AddBflInputs(Dictionary<string, object?> payload, ImageRequest request, RunwareImageProviderMetadata? options)
    {
        var model = request.Model;

        // bfl:1@2 Fill Pro (inpainting): seedImage + maskImage (mask is handled by caller)
        // bfl:1@3 Expand Pro (outpainting): seedImage + outpaint
        if (model.Equals(BflFlux1FillProModelId, StringComparison.OrdinalIgnoreCase)
            || model.Equals(BflFlux1ExpandProModelId, StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("width");
            payload.Remove("height");

            if (request.Files?.Any() == true)
            {
                var first = request.Files.First();
                payload["seedImage"] = first.Data.ToDataUrl(first.MediaType);
            }

            if (model.Equals(BflFlux1ExpandProModelId, StringComparison.OrdinalIgnoreCase) && options?.Outpaint is not null)
                payload["outpaint"] = options.Outpaint;

            return;
        }

        // bfl:2@1 Pro and bfl:2@2 Pro Ultra are text-to-image only; ignore files/mask.
        if (model.Equals(BflFlux11ProModelId, StringComparison.OrdinalIgnoreCase)
            || model.Equals(BflFlux11ProUltraModelId, StringComparison.OrdinalIgnoreCase))
        {
            payload.Remove("maskImage");
            return;
        }

        // Remaining BFL models (Kontext + FLUX.2 + dev) support referenceImages.
        if (BuildInputsReferenceImagesAsStrings(request) is { Count: > 0 } referenceImages)
            payload["referenceImages"] = referenceImages;
    }
}


