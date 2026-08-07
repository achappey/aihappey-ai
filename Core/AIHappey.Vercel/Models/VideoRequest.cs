using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Vercel.Models;

public class VideoRequest
{
    public string Model { get; set; } = null!;

    public string Prompt { get; set; } = null!;

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }

    [JsonPropertyName("aspectRatio")]
    public string? AspectRatio { get; set; }

    public int? Seed { get; set; }

    public int? Duration { get; set; }

    [JsonPropertyName("fps")]
    public int? Fps { get; set; }

    public int? N { get; set; }

    [JsonPropertyName("providerOptions")]
    public Dictionary<string, JsonElement>? ProviderOptions { get; set; }

    [JsonPropertyName("image")]
    public VideoFile? Image { get; set; }

    [JsonPropertyName("inputReferences")]
    public IEnumerable<VideoFile>? InputReferences { get; set; }

    [JsonPropertyName("frameImages")]
    public IEnumerable<VideoFrameImage>? FrameImages { get; set; }

    [JsonPropertyName("generateAudio")]
    public bool? GenerateAudio { get; set; }

}

public class VideoFrameImage
{
    [JsonPropertyName("frameType")]
    public string FrameType { get; set; } = null!;  // 'first_frame' or 'last_frame'

    public VideoFile Image { get; set; } = null!;
}

public class VideoFile
{
    public string Type { get; set; } = "file";

    public string MediaType { get; set; } = null!;

    public string Data { get; set; } = null!;
}

public class VideoResponse
{
    [JsonPropertyName("providerMetadata")]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("videos")]
    public IEnumerable<VideoResponseFile>? Videos { get; set; }

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("response")]
    public HeaderResponseData Response { get; set; } = default!;
}

public class VideoResponseFile
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "base64";

    [JsonPropertyName("data")]
    public string Data { get; set; } = null!;

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = null!;
}

/// <summary>
/// Result returned when a provider starts an asynchronous video operation.
/// The operation is provider-local until the API controller prefixes it with
/// the provider identifier for transport to the client.
/// </summary>
public sealed class VideoOperationStartResult
{
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = null!;

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("response")]
    public HeaderResponseData Response { get; set; } = default!;
}

/// <summary>
/// Common response data returned while checking an asynchronous video operation.
/// Concrete pending, completed, and error types preserve the Vercel V4
/// discriminated-union JSON shape.
/// </summary>
public abstract class VideoOperationStatusResult
{
    [JsonPropertyName("status")]
    public abstract string Status { get; }

    [JsonPropertyName("providerMetadata")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, JsonElement>? ProviderMetadata { get; set; }

    [JsonPropertyName("response")]
    public HeaderResponseData Response { get; set; } = default!;
}

public sealed class VideoOperationPendingResult : VideoOperationStatusResult
{
    public override string Status => "pending";

    [JsonPropertyName("warnings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IEnumerable<object>? Warnings { get; set; }
}

public sealed class VideoOperationCompletedResult : VideoOperationStatusResult
{
    public override string Status => "completed";

    [JsonPropertyName("videos")]
    public IEnumerable<VideoOperationVideoData> Videos { get; set; } = [];

    [JsonPropertyName("warnings")]
    public IEnumerable<object> Warnings { get; set; } = [];
}

public sealed class VideoOperationErrorResult : VideoOperationStatusResult
{
    public override string Status => "error";

    [JsonPropertyName("error")]
    public string Error { get; set; } = null!;
}

/// <summary>
/// Vercel VideoModelV4 video data. URL results use <see cref="Url"/>;
/// base64 and binary results use <see cref="Data"/>. Binary values may be
/// represented by a byte array and are serialized by System.Text.Json.
/// </summary>
public sealed class VideoOperationVideoData
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = null!;

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("data")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Data { get; set; }

    [JsonPropertyName("mediaType")]
    public string MediaType { get; set; } = null!;
}
