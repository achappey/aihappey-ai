namespace AIHappey.Core.Providers.Neuralwatt;

internal static class NeuralwattExtensions
{
    internal static IReadOnlyDictionary<string, string>? MergeRequestHeaders(
        IReadOnlyDictionary<string, string>? first,
        IReadOnlyDictionary<string, string>? second)
    {
        if ((first is null || first.Count == 0) && (second is null || second.Count == 0))
            return null;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (first is not null)
        {
            foreach (var (key, value) in first)
                merged[key] = value;
        }

        if (second is not null)
        {
            foreach (var (key, value) in second)
                merged[key] = value;
        }

        return merged;
    }
}
