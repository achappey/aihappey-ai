namespace AIHappey.AzureAuth;

public class AIServiceConfig
{
    public ProviderConfig? AzureOpenAI { get; set; }
    public ProviderConfig? OpenAI { get; set; }
    public ProviderConfig? Google { get; set; }
    public ProviderConfig? Anthropic { get; set; }
    public ProviderConfig? Perplexity { get; set; }
    public ProviderConfig? Mistral { get; set; }
    public ProviderConfig? Groq { get; set; }
    public ProviderConfig? XAI { get; set; }
    public ProviderConfig? Together { get; set; }
    public ProviderConfig? Cohere { get; set; }
    public ProviderConfig? Jina { get; set; }
    public ProviderConfig? Runway { get; set; }
    public ProviderConfig? AIML { get; set; }
    public ProviderConfig? Nscale { get; set; }
    public ProviderConfig? Novita { get; set; }
    public ProviderConfig? Cerebras { get; set; }
    public ProviderConfig? SambaNova { get; set; }
    public ProviderConfig? Fireworks { get; set; }
    public ProviderConfig? Hyperbolic { get; set; }
    public ProviderConfig? Zai { get; set; }
    public ProviderConfig? Scaleway { get; set; }
    public ProviderConfig? StabilityAI { get; set; }
}

public class ProviderConfig
{
    public string? ModelId { get; set; }
    public string ApiKey { get; set; } = null!;
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public float? Priority { get; set; }
}

