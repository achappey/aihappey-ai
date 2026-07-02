namespace AIHappey.Core.Models;

public sealed class ModelResolverOptions
{
    public string[] DisabledModels { get; set; } = [];

    public static string GetDisabledModelMessage(string model)
        => $"The system administrator has disabled use for the model '{model}'.";

    public bool IsModelDisabled(string? requestedModel, params string?[] resolvedModelAliases)
    {
        if (DisabledModels.Length == 0)
            return false;

        var candidates = GetModelCandidates(requestedModel)
            .Concat(resolvedModelAliases.SelectMany(GetModelCandidates))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return DisabledModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Select(model => model.Trim())
            .Any(candidates.Contains);
    }

    private static IEnumerable<string> GetModelCandidates(string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            yield break;

        var trimmed = model.Trim();
        yield return trimmed;

        var separatorIndex = trimmed.IndexOf('/', StringComparison.Ordinal);
        if (separatorIndex >= 0 && separatorIndex < trimmed.Length - 1)
            yield return trimmed[(separatorIndex + 1)..];
    }
}
