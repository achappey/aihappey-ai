using System.Text.Json.Serialization;

namespace AIHappey.Common.Model.Providers;

public class NovitaTranscriptionProviderMetadata
{
    [JsonPropertyName("prompt")]
    public string? Prompt { get; set; }

    [JsonPropertyName("hotwords")]
    public IEnumerable<string>? Hotwords { get; set; }
}

public sealed class NovitaSpeechProviderMetadata
{
    [JsonPropertyName("glm")]
    public NovitaGlmSpeechProviderMetadata? Glm { get; set; }

    [JsonPropertyName("txt2speech")]
    public NovitaText2SpeechSpeechProviderMetadata? Text2Speech { get; set; }

    [JsonPropertyName("minimax")]
    public NovitaMiniMaxSpeechProviderMetadata? MiniMax { get; set; }
}


public sealed class NovitaMiniMaxSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }          // Emily/James/Olivia/Michael/Sarah/John :contentReference[oaicite:5]{index=5}

    [JsonPropertyName("language_boost")]
    public string? LanguageBoost { get; set; }         // en-US/zh-CN/ja-JP :contentReference[oaicite:6]{index=6}

    [JsonPropertyName("format")]
    public string? Format { get; set; }// wav/mp3 :contentReference[oaicite:7]{index=7}

    [JsonPropertyName("vol")]
    public double? Vol { get; set; }           // 1.0..2.0 :contentReference[oaicite:8]{index=8}

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }            // 0.8..3.0 :contentReference[oaicite:9]{index=9}

    [JsonPropertyName("emotion")]
    public string? Emotion { get; set; }

    [JsonPropertyName("pitch")]
    public int? Pitch { get; set; }

}

public sealed class NovitaText2SpeechSpeechProviderMetadata
{
    [JsonPropertyName("voice_id")]
    public string? VoiceId { get; set; }          // Emily/James/Olivia/Michael/Sarah/John :contentReference[oaicite:5]{index=5}

    [JsonPropertyName("language")]
    public string? Language { get; set; }         // en-US/zh-CN/ja-JP :contentReference[oaicite:6]{index=6}


    [JsonPropertyName("volume")]
    public double? Volume { get; set; }           // 1.0..2.0 :contentReference[oaicite:8]{index=8}

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }            // 0.8..3.0 :contentReference[oaicite:9]{index=9}
}

public sealed class NovitaGlmSpeechProviderMetadata
{
    [JsonPropertyName("voice")]
    public string? Voice { get; set; }          // Emily/James/Olivia/Michael/Sarah/John :contentReference[oaicite:5]{index=5}

    [JsonPropertyName("response_format")]
    public string? ResponseFormat { get; set; }// wav/mp3 :contentReference[oaicite:7]{index=7}

    [JsonPropertyName("volume")]
    public double? Volume { get; set; }           // 1.0..2.0 :contentReference[oaicite:8]{index=8}

    [JsonPropertyName("speed")]
    public double? Speed { get; set; }            // 0.8..3.0 :contentReference[oaicite:9]{index=9}

    [JsonPropertyName("watermark_enabled")]
    public bool? WatermarkEnabled { get; set; }
}



public class NovitaImageProviderMetadata
{


}

public class NovitaProviderMetadata
{

    [JsonPropertyName("separate_reasoning")]
    public bool? SeparateReasoning { get; set; }

    [JsonPropertyName("enable_thinking")]
    public bool? EnableThinking { get; set; }

}
