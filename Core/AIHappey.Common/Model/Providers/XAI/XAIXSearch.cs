using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.XAI;

public sealed class XAIXSearch
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "x_search";

    [JsonPropertyName("allowed_x_handles")]
    public List<string>? AllowedXHandles { get; set; }

    [JsonPropertyName("excluded_x_handles")]
    public List<string>? ExcludedXHandles { get; set; }

    [JsonPropertyName("enable_image_understanding")]
    public bool? EnableImageUnderstanding { get; set; }

    [JsonPropertyName("enable_video_understanding")]
    public bool? EnableVideoUnderstanding { get; set; }

    [JsonPropertyName("from_date")]
    public DateTime? FromDate { get; set; }

    [JsonPropertyName("to_date")]
    public DateTime? ToDate { get; set; }
}

