using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraHexgradSpeechProviderMetadata
{ 
    /// <summary>
    /// Output format for the speech. Allowed values: mp3, opus, flac, wav, pcm.
    /// </summary>
    [JsonPropertyName("output_format")]
    public string? OutputFormat { get; set; }

    [JsonPropertyName("preset_voice")]
    public IEnumerable<string>? PresetVoice { get; set; }

    [JsonPropertyName("speed")]
    public float? Speed { get; set; }

    [JsonPropertyName("return_timestamps")]
    public bool? ReturnTimestamps { get; set; }

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; }

    [JsonPropertyName("target_min_tokens")]
    public int? TargetMinTokens { get; set; }

    [JsonPropertyName("target_max_tokens")]
    public int? TargetMaxTokens { get; set; }

    [JsonPropertyName("absolute_max_tokens")]
    public int? AbsoluteMaxTokens { get; set; }
}
