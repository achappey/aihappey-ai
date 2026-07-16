using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Models;

/// <summary>
/// OpenAI compatible request DTO for <c>POST /v1/images/generations</c>.
/// </summary>
public class OpenAIImageGenerationRequest
{
    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = null!;

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Model { get; set; } = null!;

    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moderation { get; set; }

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? N { get; set; }

    [JsonPropertyName("output_compression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputCompression { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("partial_images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialImages { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("style")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Style { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }
}

/// <summary>
/// OpenAI compatible request DTO for <c>POST /v1/images/edits</c>.
/// Supports both JSON image references and multipart form file uploads.
/// </summary>
public class OpenAIImageEditRequest
{
    [JsonPropertyName("images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageReference[]? Images { get; set; }

    [JsonIgnore]
    public IFormFile[]? ImageFiles { get; set; }

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = null!;

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("input_fidelity")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InputFidelity { get; set; }

    [JsonPropertyName("mask")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageReference? Mask { get; set; }

    [JsonIgnore]
    public IFormFile? MaskFile { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Model { get; set; } = null!;

    [JsonPropertyName("moderation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Moderation { get; set; }

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? N { get; set; }

    [JsonPropertyName("output_compression")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? OutputCompression { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("partial_images")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PartialImages { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("stream")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Stream { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }
}

/// <summary>
/// OpenAI compatible request DTO for <c>POST /v1/images/variations</c>.
/// </summary>
public class OpenAIImageVariationRequest
{
    [JsonPropertyName("image")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageReference? Image { get; set; }

    [JsonIgnore]
    public IFormFile? ImageFile { get; set; }

    [JsonPropertyName("model")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string Model { get; set; } = null!;

    [JsonPropertyName("n")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? N { get; set; }

    [JsonPropertyName("response_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("user")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? User { get; set; }
}

public class OpenAIImageReference
{
    [JsonPropertyName("file_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FileId { get; set; }

    [JsonPropertyName("image_url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageUrl { get; set; }
}

public class OpenAIImagesResponse
{
    [JsonPropertyName("created")]
    public long Created { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<OpenAIImageData>? Data { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageUsage? Usage { get; set; }
}

public class OpenAIImageData
{
    [JsonPropertyName("b64_json")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? B64Json { get; set; }

    [JsonPropertyName("revised_prompt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RevisedPrompt { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }
}

public class OpenAIImageUsage
{
    [JsonPropertyName("input_tokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("input_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageTokenDetails? InputTokensDetails { get; set; }

    [JsonPropertyName("output_tokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int? TotalTokens { get; set; }

    [JsonPropertyName("output_tokens_details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageTokenDetails? OutputTokensDetails { get; set; }
}

public class OpenAIImageTokenDetails
{
    [JsonPropertyName("image_tokens")]
    public int? ImageTokens { get; set; }

    [JsonPropertyName("text_tokens")]
    public int? TextTokens { get; set; }
}

public interface IOpenAIImageStreamEvent
{
    [JsonPropertyName("type")]
    string Type { get; }
}

public abstract class OpenAIImageStreamEventBase : IOpenAIImageStreamEvent
{
    [JsonPropertyName("type")]
    public abstract string Type { get; }

    [JsonPropertyName("b64_json")]
    public required string B64Json { get; set; }

    [JsonPropertyName("created_at")]
    public long CreatedAt { get; set; }

    [JsonPropertyName("size")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Size { get; set; }

    [JsonPropertyName("quality")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Quality { get; set; }

    [JsonPropertyName("background")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Background { get; set; }

    [JsonPropertyName("output_format")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputFormat { get; set; }
}

public sealed class OpenAIImageGenerationPartialImage : OpenAIImageStreamEventBase
{
    public override string Type => "image_generation.partial_image";

    [JsonPropertyName("partial_image_index")]
    public int PartialImageIndex { get; set; }
}

public sealed class OpenAIImageGenerationCompleted : OpenAIImageStreamEventBase
{
    public override string Type => "image_generation.completed";

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageUsage? Usage { get; set; }
}

public sealed class OpenAIImageEditPartialImage : OpenAIImageStreamEventBase
{
    public override string Type => "image_edit.partial_image";

    [JsonPropertyName("partial_image_index")]
    public int PartialImageIndex { get; set; }
}

public sealed class OpenAIImageEditCompleted : OpenAIImageStreamEventBase
{
    public override string Type => "image_edit.completed";

    [JsonPropertyName("usage")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAIImageUsage? Usage { get; set; }
}
