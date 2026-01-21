using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
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
            "tinfoil" => _config.Tinfoil?.ApiKey,
            "runware" => _config.Runware?.ApiKey,
            "nebius" => _config.Nebius?.ApiKey,
            "deepinfra" => _config.DeepInfra?.ApiKey,
            "deepseek" => _config.DeepSeek?.ApiKey,
            "inferencenet" => _config.Inferencenet?.ApiKey,
            "cloudrift" => _config.CloudRift?.ApiKey,
            "baseten" => _config.Baseten?.ApiKey,
            "azure" => _config.Azure?.ApiKey,
            "asyncai" => _config.AsyncAI?.ApiKey,
            "replicate" => _config.Replicate?.ApiKey,
            "contextualai" => _config.ContextualAI?.ApiKey,
            "voyageai" => _config.VoyageAI?.ApiKey,
            "minimax" => _config.MiniMax?.ApiKey,
            "deepgram" => _config.Deepgram?.ApiKey,
            "assemblyai" => _config.AssemblyAI?.ApiKey,
            "sarvam" => _config.Sarvam?.ApiKey,
            "kernelmemory" => _config.KernelMemory?.ApiKey,
            "resembleai" => _config.ResembleAI?.ApiKey,
            "speechify" => _config.Speechify?.ApiKey,
            "ttsreader" => _config.TTSReader?.ApiKey,
            "speechmatics" => _config.Speechmatics?.ApiKey,
            "hyperstack" => _config.Hyperstack?.ApiKey,
            "gladia" => _config.Gladia?.ApiKey,
            "verda" => _config.Verda?.ApiKey,
            "audixa" => _config.Audixa?.ApiKey,
            "freepik" => _config.Freepik?.ApiKey,
            "ai21" => _config.AI21?.ApiKey,
            _ => null
        };
}
