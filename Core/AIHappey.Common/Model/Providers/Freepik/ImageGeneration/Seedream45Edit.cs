using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class Seedream45Edit
{
    [JsonPropertyName("enable_safety_checker")]
    public bool? EnableSafetyChecker { get; set; }

}


