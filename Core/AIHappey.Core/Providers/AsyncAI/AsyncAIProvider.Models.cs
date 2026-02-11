using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AsyncAI;

public partial class AsyncAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                OwnedBy = "asyncAI",
                Type = "speech",
                Name = "AsyncFlow V2 (English)",
                Id = "asyncflow_v2.0".ToModelId(GetIdentifier())
            },
            new()
            {
                OwnedBy = "asyncAI",
                Type = "speech",
                Name = "AsyncFlow Multilingual V1",
                Id = "asyncflow_multilingual_v1.0".ToModelId(GetIdentifier())
            }
        ]);
    }

}

