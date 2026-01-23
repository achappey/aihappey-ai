using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.Bria;

// Minimal DTOs for Bria /v2 image endpoints.
// Keep these small and endpoint-focused so adding more models later is just:
// - add modelId in BriaProvider.Models
// - add request builder in BriaProvider.Images
// - reuse the same response parsing.

internal sealed class BriaGenerateRequest
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("images")]
    public List<string>? Images { get; set; }

    [JsonPropertyName("structured_prompt")]
    public string? StructuredPrompt { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("guidance_scale")]
    public int? GuidanceScale { get; set; }

    [JsonPropertyName("model_version")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("steps_num")]
    public int? StepsNum { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("ip_signal")]
    public bool? IpSignal { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaEditRequest
{
    [JsonPropertyName("images")]
    public List<string> Images { get; set; } = [];

    [JsonPropertyName("instruction")]
    public string? Instruction { get; set; }

    [JsonPropertyName("mask")]
    public string? Mask { get; set; }

    [JsonPropertyName("structured_instruction")]
    public string? StructuredInstruction { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("guidance_scale")]
    public int? GuidanceScale { get; set; }

    [JsonPropertyName("model_version")]
    public string? ModelVersion { get; set; }

    [JsonPropertyName("steps_num")]
    public int? StepsNum { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("ip_signal")]
    public bool? IpSignal { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaEditByTextRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = null!;
}

internal sealed class BriaEraseByTextRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("object_name")]
    public string ObjectName { get; set; } = null!;
}

internal sealed class BriaBlendRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("instruction")]
    public string Instruction { get; set; } = null!;
}

internal sealed class BriaReseasonRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("season")]
    public string Season { get; set; } = null!;
}

internal sealed class BriaReplaceTextRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("new_text")]
    public string NewText { get; set; } = null!;
}

internal sealed class BriaSketchToImageRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;
}

internal sealed class BriaRestoreRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }
}

internal sealed class BriaColorizeRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("style")]
    public string Style { get; set; } = null!;

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }
}

internal sealed class BriaRestyleRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("style")]
    public string Style { get; set; } = null!;

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }
}

internal sealed class BriaRelightRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("light_direction")]
    public string? LightDirection { get; set; }

    [JsonPropertyName("light_type")]
    public string LightType { get; set; } = null!;

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }
}

internal sealed class BriaEraseRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("mask")]
    public string Mask { get; set; } = null!;

    [JsonPropertyName("mask_type")]
    public string? MaskType { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaGenFillRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("mask")]
    public string Mask { get; set; } = null!;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = null!;

    [JsonPropertyName("version")]
    public int? Version { get; set; }

    [JsonPropertyName("refine_prompt")]
    public bool? RefinePrompt { get; set; }

    [JsonPropertyName("tailored_model_id")]
    public string? TailoredModelId { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("mask_type")]
    public string? MaskType { get; set; }
}

internal sealed class BriaRemoveBackgroundRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaReplaceBackgroundRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("ref_images")]
    public object? RefImages { get; set; }

    [JsonPropertyName("enhance_ref_images")]
    public bool? EnhanceRefImages { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("refine_prompt")]
    public bool? RefinePrompt { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("original_quality")]
    public bool? OriginalQuality { get; set; }

    [JsonPropertyName("force_background_detection")]
    public bool? ForceBackgroundDetection { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }
}

internal sealed class BriaEraseForegroundRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaBlurBackgroundRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("scale")]
    public int? Scale { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaExpandImageRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("aspect_ratio")]
    public string? AspectRatio { get; set; }

    [JsonPropertyName("canvas_size")]
    public int[]? CanvasSize { get; set; }

    [JsonPropertyName("original_image_size")]
    public int[]? OriginalImageSize { get; set; }

    [JsonPropertyName("original_image_location")]
    public int[]? OriginalImageLocation { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("prompt_content_moderation")]
    public bool? PromptContentModeration { get; set; }

    [JsonPropertyName("negative_prompt")]
    public string? NegativePrompt { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaEnhanceImageRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("steps_num")]
    public int? StepsNum { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }
}

internal sealed class BriaIncreaseResolutionRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("desired_increase")]
    public int? DesiredIncrease { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaCropForegroundRequest
{
    [JsonPropertyName("image")]
    public string Image { get; set; } = null!;

    [JsonPropertyName("padding")]
    public int? Padding { get; set; }

    [JsonPropertyName("force_background_detection")]
    public bool? ForceBackgroundDetection { get; set; }

    [JsonPropertyName("preserve_alpha")]
    public bool? PreserveAlpha { get; set; }

    [JsonPropertyName("sync")]
    public bool? Sync { get; set; }

    [JsonPropertyName("visual_input_content_moderation")]
    public bool? VisualInputContentModeration { get; set; }

    [JsonPropertyName("visual_output_content_moderation")]
    public bool? VisualOutputContentModeration { get; set; }
}

internal sealed class BriaResultEnvelope
{
    // Present for status polling responses (async workflows)
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    // Present for ERROR/UNKNOWN async workflows
    [JsonPropertyName("error")]
    public BriaError? Error { get; set; }

    [JsonPropertyName("result")]
    public BriaResult? Result { get; set; }

    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("warning")]
    public string? Warning { get; set; }

    // Only present for async (202)
    [JsonPropertyName("status_url")]
    public string? StatusUrl { get; set; }
}

internal sealed class BriaError
{
    // Bria docs describe an "error object"; shape may evolve, so keep it flexible.
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }

    [JsonPropertyName("details")]
    public string? Details { get; set; }
}

internal sealed class BriaResult
{
    [JsonPropertyName("image_url")]
    public string? ImageUrl { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    // generate endpoints
    [JsonPropertyName("structured_prompt")]
    public string? StructuredPrompt { get; set; }

    // edit endpoints
    [JsonPropertyName("structured_instruction")]
    public string? StructuredInstruction { get; set; }
}

