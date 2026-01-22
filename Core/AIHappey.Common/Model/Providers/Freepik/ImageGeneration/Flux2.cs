using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Freepik.ImageGeneration;

public sealed class Flux2
{
    [JsonPropertyName("prompt_upsampling")]
    public bool? PromptUpsampling { get; set; }

}

