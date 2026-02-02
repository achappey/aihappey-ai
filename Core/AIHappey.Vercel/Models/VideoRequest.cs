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
    public ResponseData Response { get; set; } = default!;
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