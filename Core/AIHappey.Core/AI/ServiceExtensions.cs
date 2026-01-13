using AIHappey.Core.Providers.AIML;
using AIHappey.Core.Providers.Alibaba;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Core.Providers.Cerebras;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Core.Providers.CanopyWave;
using AIHappey.Core.Providers.CloudRift;
using AIHappey.Core.Providers.DeepInfra;
using AIHappey.Core.Providers.DeepSeek;
using AIHappey.Core.Providers.Deepgram;
using AIHappey.Core.Providers.Echo;
using AIHappey.Core.Providers.Fireworks;
using AIHappey.Core.Providers.Google;
using AIHappey.Core.Providers.Groq;
using AIHappey.Core.Providers.Hyperbolic;
using AIHappey.Core.Providers.Inferencenet;
using AIHappey.Core.Providers.ElevenLabs;
using AIHappey.Core.Providers.Jina;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Core.Providers.Novita;
using AIHappey.Core.Providers.Nvidia;
using AIHappey.Core.Providers.Nscale;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Core.Providers.Nebius;
using AIHappey.Core.Providers.Runware;
using AIHappey.Core.Providers.Runway;
using AIHappey.Core.Providers.SambaNova;
using AIHappey.Core.Providers.Scaleway;
using AIHappey.Core.Providers.StabilityAI;
using AIHappey.Core.Providers.Replicate;
using AIHappey.Core.Providers.Baseten;
using AIHappey.Core.Providers.Together;
using AIHappey.Core.Providers.Telnyx;
using AIHappey.Core.Providers.Tinfoil;
using AIHappey.Core.Providers.xAI;
using AIHappey.Core.Providers.Zai;
using AIHappey.Core.Providers.Azure;
using AIHappey.Core.Providers.AsyncAI;
using Microsoft.Extensions.DependencyInjection;
using AIHappey.Core.Providers.VoyageAI;
using AIHappey.Core.Providers.ContextualAI;
using AIHappey.Core.Providers.Sarvam;
using AIHappey.Core.Providers.MiniMax;
using AIHappey.Core.Providers.AssemblyAI;
using Microsoft.KernelMemory;
using AIHappey.Core.Providers.ResembleAI;
using AIHappey.Core.Providers.Speechify;

namespace AIHappey.Core.AI;

public static class ServiceExtensions
{
    public static void AddProviders(this IServiceCollection services)
    {
        services.AddSingleton<IModelProvider, EchoProvider>();
        services.AddSingleton<IModelProvider, OpenAIProvider>();
        services.AddSingleton<IModelProvider, CloudRiftProvider>();
        services.AddSingleton<IModelProvider, TinfoilProvider>();
        services.AddSingleton<IModelProvider, DeepInfraProvider>();
        services.AddSingleton<IModelProvider, DeepSeekProvider>();
        services.AddSingleton<IModelProvider, DeepgramProvider>();
        services.AddSingleton<IModelProvider, NvidiaProvider>();
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
        services.AddSingleton<IModelProvider, RunwareProvider>();
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
        services.AddSingleton<IModelProvider, NebiusProvider>();
        services.AddSingleton<IModelProvider, ReplicateProvider>();
        services.AddSingleton<IModelProvider, BasetenProvider>();
        services.AddSingleton<IModelProvider, AzureProvider>();
        services.AddSingleton<IModelProvider, AsyncAIProvider>();
        services.AddSingleton<IModelProvider, VoyageAIProvider>();
        services.AddSingleton<IModelProvider, ContextualAIProvider>();
        services.AddSingleton<IModelProvider, SarvamProvider>();
        services.AddSingleton<IModelProvider, MiniMaxProvider>();
        services.AddSingleton<IModelProvider, AssemblyAIProvider>();
        services.AddSingleton<IModelProvider, ResembleAIProvider>();
        services.AddSingleton<IModelProvider, SpeechifyAIProvider>();
    }

    public static IServiceCollection AddKernelMemoryWithOptions(
        this IServiceCollection services,
        Action<IKernelMemoryBuilder> configure,
        KernelMemoryBuilderBuildOptions buildOptions)
    {
        // 1. Maak een nieuwe builder
        var builder = new KernelMemoryBuilder(services);

        // 2. Voer de configuratie uit
        configure(builder);

        // 3. Bouw met je eigen opties
        var memoryClient = builder.Build(buildOptions);

        // 4. Registreer de client
        services.AddSingleton(memoryClient);

        return services;
    }

}
