using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.AIML;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;

namespace AIHappey.Core.Providers.AIML;

public static class AIMLExtensions
{
    public static string GetIdentifier() => nameof(AIML).ToLowerInvariant();

    /// <summary>
    /// Builds the AIML <c>POST /v1/tts</c> payload from raw provider options and unified
    /// speech fields. Raw <c>providerOptions.aiml</c> values are passed through first, then
    /// non-null/non-empty unified fields are overlaid so the public request contract remains
    /// authoritative for shared fields.
    /// </summary>
    public static Dictionary<string, object?> GetTtsRequestPayload(this SpeechRequest request, string providerId)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Model))
            throw new ArgumentException("Model is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Text))
            throw new ArgumentException("Text is required.", nameof(request));

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (request.ProviderOptions is not null
            && request.ProviderOptions.TryGetValue(providerId, out var providerOptions)
            && providerOptions.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in providerOptions.EnumerateObject())
                payload[property.Name] = property.Value.Clone();
        }

        payload["model"] = request.Model.Trim();
        payload["text"] = request.Text;

        if (!string.IsNullOrWhiteSpace(request.Voice))
            payload["voice"] = request.Voice.Trim();

        if (!string.IsNullOrWhiteSpace(request.OutputFormat))
            ApplySpeechOutputFormat(payload, request.OutputFormat.Trim());

        return payload;
    }

    private static void ApplySpeechOutputFormat(Dictionary<string, object?> payload, string outputFormat)
    {
        var applied = false;

        foreach (var key in new[] { "response_format", "output_format", "format" })
        {
            if (!payload.ContainsKey(key))
                continue;

            payload[key] = outputFormat;
            applied = true;
        }

        if (!applied)
            payload["format"] = outputFormat;
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
