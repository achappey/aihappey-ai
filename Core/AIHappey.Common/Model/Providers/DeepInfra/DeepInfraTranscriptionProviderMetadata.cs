using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

/// <summary>
/// Provider-specific options for DeepInfra automatic speech recognition (ASR).
/// DeepInfra endpoint: <c>POST /v1/inference/{model}</c> using multipart form fields.
/// </summary>
public sealed class DeepInfraTranscriptionProviderMetadata
{
    /// <summary>
    /// Task to perform.
    /// Default: <c>transcribe</c>.
    /// Allowed: <c>transcribe</c>, <c>translate</c>.
    /// </summary>
    [JsonPropertyName("task")]
    public string? Task { get; set; }

    /// <summary>
    /// Optional text to provide as a prompt for the first window.
    /// </summary>
    [JsonPropertyName("initial_prompt")]
    public string? InitialPrompt { get; set; }

    /// <summary>
    /// Temperature to use for sampling.
    /// Default: 0.
    /// </summary>
    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    /// <summary>
    /// Language that the audio is in (ISO 639-1). If not provided, DeepInfra may auto-detect.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// Chunk level: <c>segment</c> or <c>word</c>.
    /// Default: <c>segment</c>.
    /// </summary>
    [JsonPropertyName("chunk_level")]
    public string? ChunkLevel { get; set; }

    /// <summary>
    /// Chunk length in seconds to split audio.
    /// Range: 1..30 (per DeepInfra docs), default 30.
    /// </summary>
    [JsonPropertyName("chunk_length_s")]
    public int? ChunkLengthSeconds { get; set; }
}

