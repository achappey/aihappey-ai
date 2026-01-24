using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.AIML;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.AIML;

public static class AIMLExtensions
{
    public static string GetIdentifier() => nameof(AIML).ToLowerInvariant();

    /// <summary>
    /// Best-effort payload builder for AIML audio generation models routed via the unified
    /// <see cref="SpeechRequest"/>.
    /// </summary>
    public static object GetAudioRequestPayload(this SpeechRequest request, AIMLSpeechProviderMetadata? metadata, List<object> warnings)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        // Model-aware payload mapping (similar to image payload switching).
        return request.Model switch
        {
            // MiniMax music: supports lyrics + audio_setting.
            "minimax/music-2.0" => new
            {
                model = request.Model,
                prompt = request.Text,
                lyrics = metadata?.MiniMax?.Lyrics,
                audio_setting = metadata?.MiniMax?.AudioSetting?.ValueKind is null or System.Text.Json.JsonValueKind.Null or System.Text.Json.JsonValueKind.Undefined
                    ? null
                    : metadata!.MiniMax?.AudioSetting
            },

            // Stability Audio: supports prompt + timing + steps.
            "stable-audio" => BuildStableAudioPayload(request, metadata, warnings),

            // Default: send minimal common fields.
            _ => new
            {
                model = request.Model,
                prompt = request.Text
            }
        };
    }

    private static object BuildStableAudioPayload(SpeechRequest request, AIMLSpeechProviderMetadata? metadata, List<object> warnings)
    {
        return new
        {
            model = request.Model,
            prompt = request.Text,
            seconds_total = metadata?.StabilityAI?.SecondsTotal,
            seconds_start = metadata?.StabilityAI?.SecondsStart,
            steps = metadata?.StabilityAI?.Steps
        };
    }

    public static object GetImageRequestPayload(this ImageRequest imageRequest)
    {
        var width = imageRequest.GetImageWidth();
        var height = imageRequest.GetImageHeight();
        var metadata = imageRequest.GetProviderMetadata<AIMLImageProviderMetadata>(GetIdentifier());

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
                image_urls = imageRequest.Files?.Select(a => Common.Extensions.ImageExtensions.ToDataUrl(a.Data, a.MediaType)),
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
