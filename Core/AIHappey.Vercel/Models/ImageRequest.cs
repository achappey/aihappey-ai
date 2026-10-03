using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

/// <summary>
/// Vercel Model Gateway v3 compatible request DTO for <c>POST /v1/images/generations</c>.
/// <para>
/// This JSON shape is a public, contract-locked surface. Do not rename properties, change casing,
/// alter types, or restructure this DTO.
/// </para>
/// <para>
/// Use <see cref="ProviderOptions"/> for provider-specific inputs without changing the contract.
/// </para>
/// </summary>
public class ImageRequest
{

    public string Model { get; set; } = null!;

    public string Prompt { get; set; } = null!;

    public string? Size { get; set; }

    public string? AspectRatio { get; set; }

    public int? Seed { get; set; }

    public int? N { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    [JsonPropertyName("files")]
    public IEnumerable<ImageFile>? Files { get; set; }

    [JsonPropertyName("mask")]
    public ImageFile? Mask { get; set; }

}


[JsonConverter(typeof(ImageFileJsonConverter))]
public class ImageFile : IValidatableObject
{

    public string Type { get; set; } = "file";

    public string? MediaType { get; set; }

    public string? Data { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
        => MediaFileJson.Validate(Type, this is ImageFileUrl url ? url.Url : null, MediaType, Data);
}


[JsonConverter(typeof(ImageFileJsonConverter))]
public class ImageFileUrl : ImageFile
{
    public ImageFileUrl() => Type = "url";

    public string Url { get; set; } = null!;
}


public class ImageResponse
{
    /// <summary>
    /// Provider-specific metadata (opaque JSON).
    /// </summary>
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    /// <summary>
    /// Generated images as <c>data:image/...;base64,...</c> strings.
    /// </summary>
    [JsonPropertyName("images")]
    public IEnumerable<string>? Images { get; set; }

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("response")]
    public HeaderResponseData Response { get; set; } = default!;

    [JsonPropertyName("usage")]
    public ImageUsageData? Usage { get; set; }
}


public class HeaderResponseData
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = null!;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("headers")]
    public IDictionary<string, string>? Headers { get; set; }
}


public class ResponseData
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = null!;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    [JsonPropertyName("headers")]
    public IDictionary<string, string>? Headers { get; set; }

    [JsonPropertyName("body")]
    public object? Body { get; set; }
}


public class ImageUsageData
{
    [JsonPropertyName("inputTokens")]
    public int? InputTokens { get; set; }

    [JsonPropertyName("outputTokens")]
    public int? OutputTokens { get; set; }

    [JsonPropertyName("totalTokens")]
    public int? TotalTokens { get; set; }

}
