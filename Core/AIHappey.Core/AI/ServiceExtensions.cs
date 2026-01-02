using AIHappey.Core.Providers.AIML;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Core.Providers.Cohere;
using AIHappey.Core.Providers.Google;
using AIHappey.Core.Providers.Groq;
using AIHappey.Core.Providers.Jina;
using AIHappey.Core.Providers.Mistral;
using AIHappey.Core.Providers.Nscale;
using AIHappey.Core.Providers.OpenAI;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Core.Providers.Pollinations;
using AIHappey.Core.Providers.Runway;
using AIHappey.Core.Providers.StabilityAI;
using AIHappey.Core.Providers.Together;
using AIHappey.Core.Providers.xAI;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.AI;

public static class ServiceExtensions
{
    public static void AddProviders(this IServiceCollection services)
    {
        services.AddSingleton<IModelProvider, OpenAIProvider>();
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

        
    }

}
