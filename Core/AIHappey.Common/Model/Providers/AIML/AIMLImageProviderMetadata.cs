using System.Text.Json.Serialization;
using AIHappey.Common.Model.Providers.OpenAI;

namespace AIHappey.Common.Model.Providers.AIML;

public class AIMLImageProviderMetadata
{
    [JsonPropertyName("hunyuan")]
    public AIMLHunyuanImageSettings? Hunyuan { get; set; }

    [JsonPropertyName("openai")]
    public OpenAiImageProviderMetadata? OpenAI { get; set; }
}

