using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.NLPCloud;

public sealed class NLPCloudTranscriptionProviderMetadata
{
    [JsonPropertyName("input_language")]
    public string? InputLanguage { get; set; }
}
