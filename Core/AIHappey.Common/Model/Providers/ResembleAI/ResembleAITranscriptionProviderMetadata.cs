using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ResembleAI;

public sealed class ResembleAITranscriptionProviderMetadata
{
    /// <summary>
    /// Optional "intelligence question" to evaluate after transcription.
    /// Maps to Resemble Speech-to-Text API multipart field: <c>query</c>.
    /// </summary>
    [JsonPropertyName("query")]
    public string? Query { get; set; }
}

