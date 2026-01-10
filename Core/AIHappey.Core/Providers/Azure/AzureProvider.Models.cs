using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(_endpoint))
            return Task.FromResult<IEnumerable<Model>>([]);

        return Task.FromResult<IEnumerable<Model>>([
            new Model
            {
                OwnedBy = "Azure",
                Name = "speech-to-text",
                Type = "transcription",
                Id = "speech-to-text".ToModelId(GetIdentifier())
            }
        ]);
    }
}

