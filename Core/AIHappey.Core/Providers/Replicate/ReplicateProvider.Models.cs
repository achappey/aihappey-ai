using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Replicate;

public sealed partial class ReplicateProvider
{
    private static readonly HashSet<string> SupportedModels =
        "replicate".GetModels()
            .Select(m => NormalizeModelId(m.Id))
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static void EnsureSupportedModel(string model)
    {
        if (!SupportedModels.Contains(NormalizeModelId(model)))
            throw new NotSupportedException(
                $"Replicate model '{model}' is not supported by this backend."
            );
    }

    private static string NormalizeModelId(string model)
    {
        const string providerPrefix = "replicate/";

        return model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase)
            ? model[providerPrefix.Length..]
            : model;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await Task.FromResult<IEnumerable<Model>>(GetIdentifier().GetModels());


}

