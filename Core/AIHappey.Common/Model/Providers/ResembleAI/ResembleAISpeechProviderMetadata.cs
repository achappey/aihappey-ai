using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.ResembleAI;

/// <summary>
/// Provider-specific options for ResembleAI Text-to-Speech (POST https://f.cluster.resemble.ai/synthesize).
/// These values map directly to the Resemble synthesis JSON body.
/// </summary>
public sealed class ResembleAISpeechProviderMetadata
{
    /// <summary>
    /// Voice UUID to use for synthesis (required by Resemble).
    /// </summary>
    [JsonPropertyName("voice_uuid")]
    public string? VoiceUuid { get; set; }

    /// <summary>
    /// Optional project UUID to store the generated clip.
    /// </summary>
    [JsonPropertyName("project_uuid")]
    public string? ProjectUuid { get; set; }

    /// <summary>
    /// Optional title for the generated clip.
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// Audio precision for WAV output. Allowed values: MULAW, PCM_16, PCM_24, PCM_32.
    /// </summary>
    [JsonPropertyName("precision")]
    public string? Precision { get; set; }

    /// <summary>
    /// Output format. Allowed values: wav, mp3.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    /// <summary>
    /// Audio sample rate in Hz. Allowed values include 8000, 16000, 22050, 32000, 44100, 48000.
    /// </summary>
    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    /// <summary>
    /// Enable HD synthesis with small latency trade-off.
    /// </summary>
    [JsonPropertyName("use_hd")]
    public bool? UseHd { get; set; }
}

