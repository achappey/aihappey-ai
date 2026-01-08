using AIHappey.Common.Extensions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.AIML;

namespace AIHappey.Core.Providers.AIML;

public static class AIMLExtensions
{
    public static string GetIdentifier() => nameof(AIML).ToLowerInvariant();

    public static object GetImageRequestPayload(this ImageRequest imageRequest)
    {
        var width = imageRequest.GetImageWidth();
        var height = imageRequest.GetImageHeight();
        var metadata = imageRequest.GetImageProviderMetadata<AIMLImageProviderMetadata>(GetIdentifier());

        return imageRequest.Model switch
        {
            "alibaba/qwen-image" or "alibaba/z-image-turbo" => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                seed = imageRequest.Seed,
                output_format = "png",
                image_size = width.HasValue && height.HasValue
                    ? new
                    {
                        width,
                        height
                    } : null,
                num_images = imageRequest.N
            },
            "klingai/image-o1" => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                image_urls = imageRequest.Files?.Select(a => a.Data.ToDataUrl(a.MediaType)),
                aspect_ratio = imageRequest.AspectRatio,
                num_images = imageRequest.N
            },
            "reve/create-image" => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                aspect_ratio = imageRequest.AspectRatio,
                convert_base64_to_url = false
            },
            "x-ai/grok-2-image" => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                n = imageRequest.N,
                response_format = "b64_json"
            },
            "hunyuan/hunyuan-image-v3-text-to-image" => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                seed = imageRequest.Seed,
                negative_prompt = metadata?.Hunyuan?.NegativePrompt,
                enable_prompt_expansion = metadata?.Hunyuan?.EnablePromptExpansion,
                enable_safety_checker = metadata?.Hunyuan?.EnableSafetyChecker,
                guidance_scale = metadata?.Hunyuan?.GuidanceScale,
                num_inference_steps = metadata?.Hunyuan?.NumInferenceSteps,
                image_size = width.HasValue && height.HasValue
                    ? new
                    {
                        width,
                        height
                    } : null,
                num_images = imageRequest.N,
                sync_mode = true
            },
            _ => new
            {
                prompt = imageRequest.Prompt,
                model = imageRequest.Model,
                seed = imageRequest.Seed,
                num_images = imageRequest.N,
                response_format = "b64_json"
            },
        };
    }
}
