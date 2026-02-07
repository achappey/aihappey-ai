using AIHappey.Common.Model.Providers.OpenAI;
using AIHappey.Common.Model.Providers.Perplexity;
using AIHappey.Common.Model.Providers.Anthropic;
using AIHappey.Common.Model.Providers.Groq;
using AIHappey.Common.Model.Providers.ElevenLabs;
using AIHappey.Common.Model.Providers.XAI;
using AIHappey.Common.Model.Providers.Cohere;
using AIHappey.Common.Model.Providers.Google;
using AIHappey.Common.Model.Providers.Jina;
using AIHappey.Common.Model.Providers.Mistral;

using PollinationsProviderMetadata = AIHappey.Common.Model.Providers.Pollinations.PollinationsProviderMetadata;

using TogetherProviderMetadata = AIHappey.Common.Model.Providers.Together.TogetherProviderMetadata;

namespace AIHappey.Common.Model.Providers;

public class ProviderMetadata
{
    public OpenAiProviderMetadata? Openai { get; set; }
    public PerplexityProviderMetadata? Perplexity { get; set; }
    public AnthropicProviderMetadata? Anthropic { get; set; }
    public GoogleProviderMetadata? Google { get; set; }
    public MistralProviderMetadata? Mistral { get; set; }
    public XAIProviderMetadata? XAI { get; set; }
    public TogetherProviderMetadata? Together { get; set; }
    public CohereProviderMetadata? Cohere { get; set; }
    public GroqProviderMetadata? Groq { get; set; }
    public PollinationsProviderMetadata? Pollinations { get; set; }
    public JinaProviderMetadata? Jina { get; set; }
    public ElevenLabsProviderMetadata? ElevenLabs { get; set; }
}
