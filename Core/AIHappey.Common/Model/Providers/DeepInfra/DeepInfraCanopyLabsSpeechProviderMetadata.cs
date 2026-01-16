using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers.DeepInfra;

public sealed class DeepInfraCanopyLabsSpeechProviderMetadata
{
    /// <summary>
    /// Output format for the speech. Allowed values: mp3, opus, flac, wav, pcm.
    /// </summary>
    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }

    [JsonPropertyName("voice")]
    public string? Voice { get; set; } //tara leah jess leo dan mia zac

    [JsonPropertyName("temperature")]
    public float? Temperature { get; set; }

    [JsonPropertyName("top_p")]
    public double? TopP { get; set; }

    [JsonPropertyName("max_tokens")]
    public int? MaxTokens { get; set; }

    [JsonPropertyName("repetition_penalty")]
    public double? RepetitionPenalty { get; set; }
}
