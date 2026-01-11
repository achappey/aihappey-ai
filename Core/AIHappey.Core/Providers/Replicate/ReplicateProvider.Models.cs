using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Replicate;

public sealed partial class ReplicateProvider
{
    private static readonly List<(string Id, string Name, string Owner, string Type)> AllModels =
  [
      ..ReplicateProviderImageModels.ImageModels.Select(m => (m.Id, m.Name, m.Owner, "image")),
      ..ReplicateProviderLanguageModels.LanguageModels.Select(m => (m.Id, m.Name, m.Owner, "language")),
  ];

    private static readonly HashSet<string> SupportedModels =
        AllModels.Select(m => m.Id)
                 .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void EnsureSupportedModel(string model)
    {
        if (!SupportedModels.Contains(model))
            throw new NotSupportedException(
                $"Replicate model '{model}' is not supported by this backend."
            );
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        return await Task.FromResult(
            AllModels.Select(m => new Model
            {
                Id = m.Id.ToModelId(GetIdentifier()),
                Name = m.Name,
                OwnedBy = m.Owner,
                Type = m.Type
            }).ToList()
        );
    }

}

