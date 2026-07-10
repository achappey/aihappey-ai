using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Google;

public sealed class GoogleOmniVideoProviderMetadata
{
    [JsonPropertyName("previous_interaction_id")]
    public string? PreviousInteractionId { get; set; }

    [JsonPropertyName("previousInteractionId")]
    public string? PreviousInteractionIdCamel { get; set; }

    [JsonPropertyName("task")]
    public string? Task { get; set; }

    [JsonPropertyName("delivery")]
    public string? Delivery { get; set; }

    [JsonPropertyName("response_format")]
    public JsonElement? ResponseFormat { get; set; }

    [JsonPropertyName("responseFormat")]
    public JsonElement? ResponseFormatCamel { get; set; }

    [JsonPropertyName("generation_config")]
    public JsonElement? GenerationConfig { get; set; }

    [JsonPropertyName("generationConfig")]
    public JsonElement? GenerationConfigCamel { get; set; }

    [JsonPropertyName("video_config")]
    public JsonElement? VideoConfig { get; set; }

    [JsonPropertyName("videoConfig")]
    public JsonElement? VideoConfigCamel { get; set; }

    [JsonPropertyName("document")]
    public JsonElement? Document { get; set; }

    [JsonPropertyName("video")]
    public JsonElement? Video { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalProperties { get; set; }
}
