using System.Text.Json;
using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Model.Providers.Anthropic;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Common.Model.Providers.Mistral;

namespace AIHappey.Common.Model.Providers;

public class ProviderMetadata
{
    public OpenAiProviderMetadata? Openai { get; set; }
    public JsonElement? Perplexity { get; set; }
    public AnthropicProviderMetadata? Anthropic { get; set; }
    public GoogleProviderMetadata? Google { get; set; }
    public MistralProviderMetadata? Mistral { get; set; }
    public XAIProviderMetadata? XAI { get; set; }
    public JsonElement? Together { get; set; }
    public JsonElement? Cohere { get; set; }
    public JsonElement? Groq { get; set; }
    public JsonElement? Jina { get; set; }
    public ElevenLabsProviderMetadata? ElevenLabs { get; set; }
}
