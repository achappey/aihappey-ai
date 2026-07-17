using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Models;

/// <summary>
/// OpenAI compatible request DTO for <c>POST /v1/audio/transcriptions</c>.
/// <para>
/// This is intentionally separate from the Vercel AI SDK transcription DTO.
/// Only fields that can be bridged to the current Vercel-compatible provider
/// contract are modeled here.
/// </para>
/// </summary>
public class OpenAITranscriptionRequest
{
    [JsonPropertyName("file")]
    public IFormFile File { get; set; } = null!;

    [JsonPropertyName("model")]
    public string Model { get; set; } = null!;

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("timestamp_granularities")]
    public string[]? TimestampGranularities { get; set; }

    [JsonPropertyName("stream")]
    public bool? Stream { get; set; }

    [JsonPropertyName("include")]
    public string[]? Include { get; set; }

    [JsonPropertyName("chunking_strategy")]
    public object? ChunkingStrategy { get; set; }

    [JsonPropertyName("known_speaker_names")]
    public string[]? KnownSpeakerNames { get; set; }

    [JsonPropertyName("known_speaker_references")]
    public string[]? KnownSpeakerReferences { get; set; }
}

public sealed class AudioTranscriptionServerVadChunkingStrategy
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "server_vad";

    [JsonPropertyName("prefix_padding_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PrefixPaddingMs { get; set; }

    [JsonPropertyName("silence_duration_ms")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? SilenceDurationMs { get; set; }
}