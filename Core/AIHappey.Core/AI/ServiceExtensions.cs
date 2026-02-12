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
using AIHappey.Core.Providers.Inworld;
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
using AIHappey.Core.Providers.AI21;
using Microsoft.KernelMemory;
using AIHappey.Core.Providers.ResembleAI;
using AIHappey.Core.Providers.Speechify;
using AIHappey.Core.Providers.TTSReader;
using AIHappey.Core.Providers.Speechmatics;
using AIHappey.Core.Providers.Hyperstack;
using AIHappey.Core.Providers.Gladia;
using AIHappey.Core.Providers.Verda;
using AIHappey.Core.Providers.Audixa;
using AIHappey.Core.Providers.Freepik;
using AIHappey.Core.Providers.MurfAI;
using AIHappey.Core.Providers.Lingvanex;
using AIHappey.Core.Providers.GoogleTranslate;
using AIHappey.Core.Providers.ModernMT;
using AIHappey.Core.Providers.LectoAI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Providers.Bria;
using AIHappey.Core.Providers.Friendli;
using AIHappey.Core.Providers.PublicAI;
using AIHappey.Core.Providers.PrimeIntellect;
using AIHappey.Core.Providers.OVHcloud;
using AIHappey.Core.Providers.GTranslate;
using AIHappey.Core.Providers.GMICloud;
using AIHappey.Core.Providers.BytePlus;
using AIHappey.Core.Providers.NLPCloud;
using AIHappey.Core.Providers.Moonshot;
using AIHappey.Core.Providers.Upstage;
using AIHappey.Core.Providers.SiliconFlow;
using AIHappey.Core.Providers.Cirrascale;
using AIHappey.Core.Providers.KlingAI;
using AIHappey.Core.Providers.Euqai;
using AIHappey.Core.Providers.Vidu;
using AIHappey.Core.Providers.Reve;
using AIHappey.Core.Providers.Decart;
using AIHappey.Core.Providers.JSON2Video;
using AIHappey.Core.Providers.GreenPT;
using AIHappey.Core.Providers.RelaxAI;
using AIHappey.Core.Providers.RegoloAI;
using AIHappey.Core.Providers.ARKLabs;
using AIHappey.Core.Providers.IONOS;
using AIHappey.Core.Providers.BergetAI;
using AIHappey.Core.Providers.OpperAI;
using AIHappey.Core.Providers.Cortecs;
using Microsoft.Extensions.DependencyInjection.Extensions;
using AIHappey.Core.Providers.EUrouter;
using AIHappey.Core.Providers.Requesty;
using AIHappey.Core.Providers.Sudo;
using AIHappey.Core.Providers.Portkey;
using AIHappey.Core.Providers.DeepL;
using AIHappey.Core.Providers.CometAPI;
using AIHappey.Core.Providers.Nextbit;
using AIHappey.Core.Providers.Deepbricks;
using AIHappey.Core.Providers.Segmind;
using AIHappey.Core.Providers.RekaAI;
using AIHappey.Core.Providers.OpenRouter;
using AIHappey.Core.Providers.AI302;
using AIHappey.Core.Providers.MatterAI;
using AIHappey.Core.Providers.Bytez;

namespace AIHappey.Core.AI;

public static class ServiceExtensions
{
    public static void AddProviders(this IServiceCollection services)
    {
        services.TryAddSingleton<EndUserIdHasher>();
        services.TryAddSingleton<IEndUserIdResolver, NullEndUserIdResolver>();

        services.AddSingleton<IModelProvider, EchoProvider>();
        services.AddSingleton<IModelProvider, OpenAIProvider>();
        services.AddSingleton<IModelProvider, CloudRiftProvider>();
        services.AddSingleton<IModelProvider, TinfoilProvider>();
        services.AddSingleton<IModelProvider, DeepInfraProvider>();
        services.AddSingleton<IModelProvider, DeepSeekProvider>();
        services.AddSingleton<IModelProvider, DeepgramProvider>();
        services.AddSingleton<IModelProvider, InworldProvider>();
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
        services.AddSingleton<IModelProvider, SpeechifyProvider>();
        services.AddSingleton<IModelProvider, TTSReaderProvider>();
        services.AddSingleton<IModelProvider, SpeechmaticsProvider>();
        services.AddSingleton<IModelProvider, HyperstackProvider>();
        services.AddSingleton<IModelProvider, GladiaProvider>();
        services.AddSingleton<IModelProvider, VerdaProvider>();
        services.AddSingleton<IModelProvider, AudixaProvider>();
        services.AddSingleton<IModelProvider, AI21Provider>();
        services.AddSingleton<IModelProvider, FreepikProvider>();
        services.AddSingleton<IModelProvider, MurfAIProvider>();
        services.AddSingleton<IModelProvider, LingvanexProvider>();
        services.AddSingleton<IModelProvider, GoogleTranslateProvider>();
        services.AddSingleton<IModelProvider, ModernMTProvider>();
        services.AddSingleton<IModelProvider, LectoAIProvider>();
        services.AddSingleton<IModelProvider, BriaProvider>();
        services.AddSingleton<IModelProvider, FriendliProvider>();
        services.AddSingleton<IModelProvider, PublicAIProvider>();
        services.AddSingleton<IModelProvider, PrimeIntellectProvider>();
        services.AddSingleton<IModelProvider, OVHcloudProvider>();
        services.AddSingleton<IModelProvider, GTranslateProvider>();
        services.AddSingleton<IModelProvider, GMICloudProvider>();
        services.AddSingleton<IModelProvider, BytePlusProvider>();
        services.AddSingleton<IModelProvider, NLPCloudProvider>();
        services.AddSingleton<IModelProvider, MoonshotProvider>();
        services.AddSingleton<IModelProvider, UpstageProvider>();
        services.AddSingleton<IModelProvider, SiliconFlowProvider>();
        services.AddSingleton<IModelProvider, CirrascaleProvider>();
        services.AddSingleton<IModelProvider, KlingAIProvider>();
        services.AddSingleton<IModelProvider, EuqaiProvider>();
        services.AddSingleton<IModelProvider, ViduProvider>();
        services.AddSingleton<IModelProvider, ReveProvider>();
        services.AddSingleton<IModelProvider, DecartProvider>();
        services.AddSingleton<IModelProvider, JSON2VideoProvider>();
        services.AddSingleton<IModelProvider, GreenPTProvider>();
        services.AddSingleton<IModelProvider, RelaxAIProvider>();
        services.AddSingleton<IModelProvider, RegoloAIProvider>();
        services.AddSingleton<IModelProvider, ARKLabsProvider>();
        services.AddSingleton<IModelProvider, IONOSProvider>();
        services.AddSingleton<IModelProvider, BergetAIProvider>();
        services.AddSingleton<IModelProvider, OpperAIProvider>();
        services.AddSingleton<IModelProvider, CortecsProvider>();
        services.AddSingleton<IModelProvider, EUrouterProvider>();
        services.AddSingleton<IModelProvider, RequestyProvider>();
        services.AddSingleton<IModelProvider, SudoProvider>();
        services.AddSingleton<IModelProvider, PortkeyProvider>();
        services.AddSingleton<IModelProvider, DeepLProvider>();
        services.AddSingleton<IModelProvider, CometAPIProvider>();
        services.AddSingleton<IModelProvider, NextbitProvider>();
        services.AddSingleton<IModelProvider, DeepbricksProvider>();
        services.AddSingleton<IModelProvider, SegmindProvider>();
        services.AddSingleton<IModelProvider, RekaAIProvider>();
        services.AddSingleton<IModelProvider, OpenRouterProvider>();
        services.AddSingleton<IModelProvider, AI302Provider>();
        services.AddSingleton<IModelProvider, MatterAIProvider>();
        services.AddSingleton<IModelProvider, BytezProvider>();
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
