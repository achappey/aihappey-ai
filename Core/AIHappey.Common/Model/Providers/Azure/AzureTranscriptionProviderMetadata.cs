using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Azure;

public sealed class AzureTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }
}

