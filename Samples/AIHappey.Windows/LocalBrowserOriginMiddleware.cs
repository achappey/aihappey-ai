namespace AIHappey.Windows;

public sealed class LocalBrowserOriginMiddleware(RequestDelegate next, IReadOnlyCollection<string> allowedOrigins)
{
    private readonly HashSet<string> _allowedOrigins = allowedOrigins
        .Select(NormalizeOrigin)
        .Where(origin => origin is not null)
        .Select(origin => origin!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0)
        {
            await next(context);
            return;
        }

        var origin = origins.Count == 1 ? NormalizeOrigin(origins[0]!) : null;
        var requestOrigin = NormalizeOrigin($"{context.Request.Scheme}://{context.Request.Host}");
        if (origin is null
            || (origin != requestOrigin && !_allowedOrigins.Contains(origin)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }

    private static string? NormalizeOrigin(string origin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || uri.AbsolutePath != "/"
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.SafeUnescaped);
    }
}
