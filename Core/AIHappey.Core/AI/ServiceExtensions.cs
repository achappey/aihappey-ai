using AIHappey.Core.Providers.AIML;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Core.Providers.Cerebras;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Core.Providers.CanopyWave;
using AIHappey.Core.Providers.Fireworks;
using AIHappey.Core.Providers.Google;
using AIHappey.Core.Providers.Groq;
using AIHappey.Core.Providers.Hyperbolic;
using AIHappey.Core.Providers.Inferencenet;
using AIHappey.Core.Providers.ElevenLabs;
using AIHappey.Core.Providers.Jina;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Core.Providers.Novita;
using AIHappey.Core.Providers.Nscale;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Core.Providers.Runway;
using AIHappey.Core.Providers.SambaNova;
using AIHappey.Core.Providers.Scaleway;
using AIHappey.Core.Providers.StabilityAI;
using AIHappey.Core.Providers.Together;
using AIHappey.Core.Providers.Telnyx;
using AIHappey.Core.Providers.xAI;
using AIHappey.Core.Providers.Zai;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.AI;

public static class ServiceExtensions
{
    public static void AddProviders(this IServiceCollection services)
    {
        services.AddSingleton<IModelProvider, OpenAIProvider>();
        services.AddSingleton<IModelProvider, CanopyWaveProvider>();
        services.AddSingleton<IModelProvider, InferencenetProvider>();
        services.AddSingleton<IModelProvider, AlibabaProvider>();
        services.AddSingleton<IModelProvider, CohereProvider>();
        services.AddSingleton<IModelProvider, MistralProvider>();
        services.AddSingleton<IModelProvider, AnthropicProvider>();
        services.AddSingleton<IModelProvider, GoogleAIProvider>();
        services.AddSingleton<IModelProvider, JinaProvider>();
        services.AddSingleton<IModelProvider, GroqProvider>();
        services.AddSingleton<IModelProvider, PerplexityProvider>();
        services.AddSingleton<IModelProvider, TogetherProvider>();
        services.AddSingleton<IModelProvider, PollinationsProvider>();
        services.AddSingleton<IModelProvider, XAIProvider>();
        services.AddSingleton<IModelProvider, RunwayProvider>();
        services.AddSingleton<IModelProvider, AIMLProvider>();
        services.AddSingleton<IModelProvider, NscaleProvider>();
        services.AddSingleton<IModelProvider, StabilityAIProvider>();
        services.AddSingleton<IModelProvider, NovitaProvider>();
        services.AddSingleton<IModelProvider, ScalewayProvider>();
        services.AddSingleton<IModelProvider, SambaNovaProvider>();
        services.AddSingleton<IModelProvider, CerebrasProvider>();
        services.AddSingleton<IModelProvider, FireworksProvider>();
        services.AddSingleton<IModelProvider, HyperbolicProvider>();
        services.AddSingleton<IModelProvider, ZaiProvider>();
        services.AddSingleton<IModelProvider, ElevenLabsProvider>();
        services.AddSingleton<IModelProvider, TelnyxProvider>();

    }

}
