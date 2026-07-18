using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Core.Providers.Anthropic;
using AIHappey.Core.Providers.OpenAI;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Core.Providers.CondenseChat;

public partial class CondenseChatProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var oaiprovider = _serviceProvider.GetService<OpenAIProvider>();
                var antprovider = _serviceProvider.GetService<AnthropicProvider>();

                var openAImodels = oaiprovider != null ? await oaiprovider.ListModels(cancellationToken)
                    : [];

                var antModels = antprovider != null ?
                    await antprovider.ListModels(cancellationToken) : [];

                List<Model> allModels = [.. openAImodels, .. antModels];

                return allModels
                   .Where(a => a.Type.Equals("language") || (string.IsNullOrEmpty(a.Type)
                        && a.Id.GuessModelType().Equals("language")))
                   .Select(a => new Model()
                   {
                       Id = a.Id.ToModelId(GetIdentifier()),
                       OwnedBy = a.OwnedBy,
                       Created = a.Created,
                       ContextWindow = a.ContextWindow,
                       MaxTokens = a.MaxTokens,
                       Description = a.Description
                   });

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}