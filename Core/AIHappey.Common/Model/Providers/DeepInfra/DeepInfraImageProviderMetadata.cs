using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraImageProviderMetadata
{
    [JsonPropertyName("bria")]
    public DeepInfraBriaImageProviderMetadata? Bria { get; set; }
}

