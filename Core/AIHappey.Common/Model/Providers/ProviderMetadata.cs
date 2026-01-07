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
