using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class ZImageTurbo
{
    [JsonPropertyName("num_inference_steps")]
    public int? NumInferenceSteps { get; set; }

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

}


