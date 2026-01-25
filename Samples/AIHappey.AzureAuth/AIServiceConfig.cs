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
    public ProviderConfig? ElevenLabs { get; set; }
    public ProviderConfig? Telnyx { get; set; }
    public ProviderConfig? Alibaba { get; set; }
    public ProviderConfig? CanopyWave { get; set; }
    public ProviderConfig? NVIDIA { get; set; }
    public ProviderConfig? Runware { get; set; }
    public ProviderConfig? DeepInfra { get; set; }
    public ProviderConfig? DeepSeek { get; set; }
    public ProviderConfig? Inferencenet { get; set; }
    public ProviderConfig? CloudRift { get; set; }
    public ProviderConfig? Tinfoil { get; set; }
    public ProviderConfig? Nebius { get; set; }
    public ProviderConfig? Baseten { get; set; }
    public ProviderConfig? AsyncAI { get; set; }
    public ProviderConfig? Replicate { get; set; }
    public ProviderConfig? VoyageAI { get; set; }
    public ProviderConfig? ContextualAI { get; set; }
    public ProviderConfig? Azure { get; set; }
    public ProviderConfig? Deepgram { get; set; }
    public ProviderConfig? MiniMax { get; set; }
    public ProviderConfig? Sarvam { get; set; }
    public ProviderConfig? AssemblyAI { get; set; }
    public ProviderConfig? KernelMemory { get; set; }
    public ProviderConfig? ResembleAI { get; set; }
    public ProviderConfig? Speechify { get; set; }
    public ProviderConfig? TTSReader { get; set; }
    public ProviderConfig? Speechmatics { get; set; }
    public ProviderConfig? Hyperstack { get; set; }
    public ProviderConfig? Gladia { get; set; }
    public ProviderConfig? Verda { get; set; }
    public ProviderConfig? Audixa { get; set; }
    public ProviderConfig? Freepik { get; set; }
    public ProviderConfig? AI21 { get; set; }
    public ProviderConfig? MurfAI { get; set; }
    public ProviderConfig? Lingvanex { get; set; }
    public ProviderConfig? GoogleTranslate { get; set; }
    public ProviderConfig? ModernMT { get; set; }
    public ProviderConfig? LectoAI { get; set; }
    public ProviderConfig? Bria { get; set; }
    public ProviderConfig? Friendli { get; set; }
    public ProviderConfig? PublicAI { get; set; }
    public ProviderConfig? PrimeIntellect { get; set; }
    public ProviderConfig? OVHcloud { get; set; }
    public ProviderConfig? GMICloud { get; set; }
    public ProviderConfig? BytePlus { get; set; }
    public ProviderConfig? NLPCloud { get; set; }
    public ProviderConfig? Moonshot { get; set; }
    public ProviderConfig? Upstage { get; set; }
    public ProviderConfig? SiliconFlow { get; set; }
}

public class ProviderConfig
{
    public string ApiKey { get; set; } = null!;

    public string? Endpoint { get; set; } = null!;

    public string? DefaultModel { get; set; } = null!;
}

