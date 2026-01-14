using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model;

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


public class ImageFile
{

    public string Type { get; set; } = "file";

    public string MediaType { get; set; } = null!;

    public string Data { get; set; } = null!;
}


public class ImageFileUrl
{

    public string Type { get; set; } = "url";

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
    public ResponseData Response { get; set; } = default!;

    [JsonPropertyName("usage")]
    public ImageUsageData? Usage { get; set; }
}

public class ResponseData
{
    [JsonPropertyName("modelId")]
    public string ModelId { get; set; } = null!;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

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