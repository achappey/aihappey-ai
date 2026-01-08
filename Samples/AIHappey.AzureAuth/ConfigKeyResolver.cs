using AIHappey.Core.AI;
using Microsoft.Extensions.Options;

namespace AIHappey.AzureAuth;

public class ConfigKeyResolver(IOptions<AIServiceConfig> config) : IApiKeyResolver
{
    private readonly AIServiceConfig _config = config.Value;

    public string? Resolve(string provider)
        => provider switch
        {
            "mistral" => _config.Mistral?.ApiKey,
            "groq" => _config.Groq?.ApiKey,
            "openai" => _config.OpenAI?.ApiKey,
            "google" => _config.Google?.ApiKey,
            "cohere" => _config.Cohere?.ApiKey,
            "together" => _config.Together?.ApiKey,
            "runway" => _config.Runway?.ApiKey,
            "aiml" => _config.AIML?.ApiKey,
            "xai" => _config.XAI?.ApiKey,
            "nscale" => _config.Nscale?.ApiKey,
            "novita" => _config.Novita?.ApiKey,
            "cerebras" => _config.Cerebras?.ApiKey,
            "sambanova" => _config.SambaNova?.ApiKey,
            "fireworks" => _config.Fireworks?.ApiKey,
            "hyperbolic" => _config.Hyperbolic?.ApiKey,
            "zai" => _config.Zai?.ApiKey,
            "scaleway" => _config.Scaleway?.ApiKey,
            "stabilityai" => _config.StabilityAI?.ApiKey,
            "perplexity" => _config.Perplexity?.ApiKey,
            "jina" => _config.Jina?.ApiKey,
            "anthropic" => _config.Anthropic?.ApiKey,
            "elevenlabs" => _config.ElevenLabs?.ApiKey,
            "telnyx" => _config.Telnyx?.ApiKey,
            "alibaba" => _config.Alibaba?.ApiKey,
            "canopywave" => _config.CanopyWave?.ApiKey,
            "nvidia" => _config.NVIDIA?.ApiKey,
            "inferencenet" => _config.Inferencenet?.ApiKey,
            _ => null
        };
}
