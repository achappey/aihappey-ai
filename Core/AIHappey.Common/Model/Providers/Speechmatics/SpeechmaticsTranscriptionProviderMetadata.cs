using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.Speechmatics;

public sealed class SpeechmaticsTranscriptionProviderMetadata
{
    [JsonPropertyName("language")]
    public string? Language { get; set; }

}

