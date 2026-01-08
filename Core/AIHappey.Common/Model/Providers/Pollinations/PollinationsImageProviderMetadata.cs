using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Pollinations;

public sealed class PollinationsImageProviderMetadata
{
    [JsonPropertyName("enhance")]
    public bool? Enhance { get; set; }

    [JsonPropertyName("private")]
    public bool? Private { get; set; }
}

