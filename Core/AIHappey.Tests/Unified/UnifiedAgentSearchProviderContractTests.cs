using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Agentics;
using AIHappey.Core.Providers.AppNZ;
using AIHappey.Core.Providers.Brave;
using AIHappey.Core.Providers.DeepSeek;
using AIHappey.Core.Providers.EmpirioLabsAI;
using AIHappey.Core.Providers.IOnet;
using AIHappey.Core.Providers.PawaAI;
using AIHappey.Core.Providers.Perplexity;
using AIHappey.Core.Providers.SailResearch;
using AIHappey.Core.Providers.SambaNova;
using AIHappey.Core.Providers.SandBase;
using AIHappey.Core.Providers.Valyu;
using AIHappey.Core.Providers.VLMRun;
using AIHappey.Core.Providers.Zai;

namespace AIHappey.Tests.Unified;

public class UnifiedAgentSearchProviderContractTests
{
    [Fact]
    public void Providers_with_unified_agent_and_search_methods_advertise_the_contract()
    {
        var providerTypes = new[]
        {
            typeof(AgenticsProvider),
            typeof(AppNZProvider),
            typeof(BraveProvider),
            typeof(DeepSeekProvider),
            typeof(EmpirioLabsAIProvider),
            typeof(IOnetProvider),
            typeof(PawaAIProvider),
            typeof(PerplexityProvider),
            typeof(SailResearchProvider),
            typeof(SambaNovaProvider),
            typeof(SandBaseProvider),
            typeof(ValyuProvider),
            typeof(VLMRunProvider),
            typeof(ZaiProvider)
        };

        Assert.All(providerTypes, type =>
            Assert.True(typeof(IUnifiedModelProvider).IsAssignableFrom(type), $"{type.Name} must advertise IUnifiedModelProvider."));
    }
}
