namespace AIHappey.Windows;

public sealed class LocalBrowserOriginMiddleware(
    RequestDelegate next,
    IReadOnlyCollection<string> allowedOrigins,
    IReadOnlyCollection<string> allowedHosts)
{
    private readonly HashSet<string> _allowedOrigins = allowedOrigins
        .Select(NormalizeOrigin)
        .Where(origin => origin is not null)
        .Select(origin => origin!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowedHosts = allowedHosts
        .Select(NormalizeHost)
        .Where(host => host is not null)
        .Select(host => host!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public async Task InvokeAsync(HttpContext context)
    {
        var requestHost = NormalizeHost(context.Request.Host.Host);
        if (requestHost is null || !_allowedHosts.Contains(requestHost))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var origins = context.Request.Headers.Origin;
        if (origins.Count == 0)
        {
            var fetchSites = context.Request.Headers["Sec-Fetch-Site"];
            if (fetchSites.Count != 0
                && (fetchSites.Count != 1
                    || !string.Equals(fetchSites[0], "same-origin", StringComparison.OrdinalIgnoreCase)))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
            return;
        }

        var origin = origins.Count == 1 ? NormalizeOrigin(origins[0]!) : null;
        var requestOrigin = NormalizeOrigin($"{context.Request.Scheme}://{context.Request.Host}");
        var isSameOrigin = origin is not null && string.Equals(origin, requestOrigin, StringComparison.OrdinalIgnoreCase);
        if (origin is null
            || (!isSameOrigin && !_allowedOrigins.Contains(origin)))
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

    private static string? NormalizeHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host) || host.Contains('*'))
            return null;

        return host.Trim().Trim('[', ']').TrimEnd('.');
    }
}
