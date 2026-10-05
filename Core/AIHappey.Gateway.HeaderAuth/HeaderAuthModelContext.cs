using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth;

internal static class HeaderAuthModelContext
{
    private const string ActiveProviderItemKey = "AIHappey.HeaderAuth.ActiveProvider";

    public static void SetActiveProvider(HttpContext httpContext, string? model)
    {
        if (httpContext == null)
            return;

        if (TryGetProviderFromModel(model, out var provider))
        {
            httpContext.Items[ActiveProviderItemKey] = provider;
            return;
        }

        ClearActiveProvider(httpContext);
    }

    public static void ClearActiveProvider(HttpContext httpContext)
        => httpContext?.Items.Remove(ActiveProviderItemKey);

    public static string? TryGetActiveProvider(HttpContext httpContext)
        => httpContext.Items.TryGetValue(ActiveProviderItemKey, out var provider)
            ? provider as string
            : null;

    private static bool TryGetProviderFromModel(string? model, out string provider)
    {
        provider = string.Empty;

        if (string.IsNullOrWhiteSpace(model) || !model.Contains('/', StringComparison.Ordinal))
            return false;

        var split = model.SplitModelId();
        if (string.IsNullOrWhiteSpace(split.Provider) || string.IsNullOrWhiteSpace(split.Model))
            return false;

        provider = split.Provider;
        return true;
    }
}
