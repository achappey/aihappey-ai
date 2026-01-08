using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class PerplexityMediaResponse
{
    [JsonPropertyName("return_videos")]
    public bool? ReturnVideos { get; set; }

    [JsonPropertyName("return_images")]
    public bool? ReturnImages { get; set; }
}

