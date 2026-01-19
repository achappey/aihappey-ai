using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Azure;

public sealed class AzureTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("samplesPerSecond")]
    public int? SamplesPerSecond { get; set; }

    [JsonPropertyName("bitsPerSample")]
    public int? BitsPerSample { get; set; }

    [JsonPropertyName("channels")]
    public int? Channels { get; set; }


}

