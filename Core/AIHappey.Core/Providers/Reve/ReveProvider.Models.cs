using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Reve;

public partial class ReveProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);


        ApplyAuthHeader();

        return await Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                Id = "latest".ToModelId(GetIdentifier()),
                Name = "latest",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "latest-fast".ToModelId(GetIdentifier()),
                Name = "latest-fast",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "reve-remix@20250915".ToModelId(GetIdentifier()),
                Name = "reve-remix@20250915",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "reve-remix-fast@20251030".ToModelId(GetIdentifier()),
                Name = "reve-remix-fast@20251030",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "reve-edit@20250915".ToModelId(GetIdentifier()),
                Name = "reve-edit@20250915",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "reve-edit-fast@20251030".ToModelId(GetIdentifier()),
                Name = "reve-edit-fast@20251030",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
            new()
            {
                Id = "reve-create@20250915".ToModelId(GetIdentifier()),
                Name = "reve-create@20250915",
                Type = "image",
                OwnedBy = nameof(Reve),
            },
        ]);
    }

}

