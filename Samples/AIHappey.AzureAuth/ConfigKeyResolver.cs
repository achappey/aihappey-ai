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
            "perplexity" => _config.Perplexity?.ApiKey,
            "jina" => _config.Jina?.ApiKey,
            "anthropic" => _config.Anthropic?.ApiKey,
            _ => null
        };
}
