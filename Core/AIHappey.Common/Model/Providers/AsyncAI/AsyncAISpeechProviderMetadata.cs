using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.AsyncAI;

public sealed class AsyncAISpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public AsyncAIVoice? Voice { get; set; }

    [JsonPropertyName("output_format")]
    public AsyncAIOutputFormat? OutputFormat { get; set; }

    /// <summary>
    /// Force to synthesize speech in the specified language (ISO 639-1), regardless of detected language.
    /// </summary>
    [JsonPropertyName("language")]
    public string? Language { get; set; }

    /// <summary>
    /// (experimental) Adjusts the speaking speed of the synthesized voice. Range: 0.7..2.0
    /// </summary>
    [JsonPropertyName("speed_control")]
    public double? SpeedControl { get; set; }

    /// <summary>
    /// (experimental) Adjusts how stable or expressive the synthesized voice sounds. Range: 0..100
    /// </summary>
    [JsonPropertyName("stability")]
    public int? Stability { get; set; }
}

public sealed class AsyncAIOutputFormat
{
    [JsonPropertyName("container")]
    public string? Container { get; set; } // raw | mp3 | wav

    /// <summary>
    /// Ignore for mp3. Default per asyncAI docs: pcm_s16le
    /// </summary>
    [JsonPropertyName("encoding")]
    public string? Encoding { get; set; } // pcm_f32le | pcm_s16le

    [JsonPropertyName("sample_rate")]
    public int? SampleRate { get; set; } // 8000..48000

    /// <summary>
    /// Use only with mp3. Default per asyncAI docs: 192000
    /// </summary>
    [JsonPropertyName("bit_rate")]
    public int? BitRate { get; set; }
}


public sealed class AsyncAIVoice
{
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "id";

    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;
}


