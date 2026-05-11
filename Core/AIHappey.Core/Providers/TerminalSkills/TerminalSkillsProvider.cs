using AIHappey.Common.Model.Skills;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.TerminalSkills;

public partial class TerminalSkillsProvider : ISkillProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public TerminalSkillsProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://terminalskills.io/api/");
    }   

  

    public string GetIdentifier() => "terminalskills";

  
}
