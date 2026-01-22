using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class Flux2Turbo
{
    [JsonPropertyName("guidance_scale")]
    public float? GuidanceScale { get; set; }

    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

}


