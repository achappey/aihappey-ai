using AIHappey.Common.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.AIML;

public static class AIMLExtensions
{
    private static readonly JsonSerializerOptions ImagePayloadJsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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
        var metadata = imageRequest.GetProviderMetadata<JsonElement>(GetIdentifier());
        var optionNamespace = imageRequest.Model.StartsWith("hunyuan/", StringComparison.OrdinalIgnoreCase)
            ? "hunyuan"
            : imageRequest.Model.StartsWith("openai/", StringComparison.OrdinalIgnoreCase)
                ? "openai"
                : null;
        var hunyuan = GetHunyuanSettings(metadata);
        var payload = GetImageProviderOptions(metadata, optionNamespace);

        object requestPayload = imageRequest.Model switch
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
                negative_prompt = hunyuan.NegativePrompt,
                enable_prompt_expansion = hunyuan.EnablePromptExpansion,
                enable_safety_checker = hunyuan.EnableSafetyChecker,
                guidance_scale = hunyuan.GuidanceScale,
                num_inference_steps = hunyuan.NumInferenceSteps,
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

        var mappedPayload = JsonSerializer.SerializeToNode(requestPayload, ImagePayloadJsonOptions)!.AsObject();

        foreach (var property in mappedPayload)
            payload[property.Key] = property.Value?.DeepClone();

        return payload;
    }

    private static JsonObject GetImageProviderOptions(JsonElement metadata, string? optionNamespace)
    {
        var payload = new JsonObject();

        if (metadata.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return payload;

        if (metadata.ValueKind != JsonValueKind.Object)
            throw new JsonException("AIML image provider options must be a JSON object.");

        foreach (var property in metadata.EnumerateObject())
        {
            var isHunyuanOptions = string.Equals(property.Name, "hunyuan", StringComparison.OrdinalIgnoreCase);
            var isOpenAiOptions = string.Equals(property.Name, "openai", StringComparison.OrdinalIgnoreCase);
            var isOptionNamespace = string.Equals(property.Name, optionNamespace, StringComparison.OrdinalIgnoreCase);

            if (isHunyuanOptions || isOpenAiOptions)
            {
                if (property.Value.ValueKind == JsonValueKind.Null)
                    continue;

                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new JsonException($"AIML {property.Name} image options must be a JSON object.");

                if (!isOptionNamespace)
                    continue;

                foreach (var setting in property.Value.EnumerateObject())
                    payload[setting.Name] = JsonNode.Parse(setting.Value.GetRawText());

                continue;
            }

            payload[property.Name] = JsonNode.Parse(property.Value.GetRawText());
        }

        return payload;
    }

    private static (
        bool? EnablePromptExpansion,
        bool? EnableSafetyChecker,
        float? GuidanceScale,
        int? NumInferenceSteps,
        string? NegativePrompt) GetHunyuanSettings(JsonElement metadata)
    {
        if (metadata.ValueKind is not JsonValueKind.Object
            || !TryGetPropertyIgnoreCase(metadata, "hunyuan", out var settings)
            || settings.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (settings.ValueKind != JsonValueKind.Object)
            throw new JsonException("AIML Hunyuan image options must be a JSON object.");

        return (
            ReadHunyuanProperty<bool?>(settings, "enable_prompt_expansion"),
            ReadHunyuanProperty<bool?>(settings, "enable_safety_checker"),
            ReadHunyuanProperty<float?>(settings, "guidance_scale"),
            ReadHunyuanProperty<int?>(settings, "num_inference_steps"),
            ReadHunyuanProperty<string?>(settings, "negative_prompt"));
    }

    private static T? ReadHunyuanProperty<T>(JsonElement settings, string name)
        => TryGetPropertyIgnoreCase(settings, name, out var value)
            ? value.Deserialize<T>(JsonSerializerOptions.Web)
            : default;

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
