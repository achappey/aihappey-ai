using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Perplexity;

public sealed class PerplexityMediaResponse
{
    [JsonPropertyName("enable_media_classifier")]
    public bool? EnableMediaClassifier { get; set; }

    [JsonPropertyName("overrides")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PerplexityMediaResponseOverrides? Overrides { get; set; }
}


public sealed class PerplexityMediaResponseOverrides
{
    [JsonPropertyName("return_videos")]
    public bool? ReturnVideos { get; set; }

    [JsonPropertyName("return_images")]
    public bool? ReturnImages { get; set; }
}

