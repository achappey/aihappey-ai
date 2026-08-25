using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Orchestration;

namespace AIHappey.HeaderAuth.Middleware;

/// <summary>
/// Converts unresolved models caused by absent request credentials into an
/// OpenAI-compatible authentication response. Provider resolution always runs
/// first so providers that work without credentials remain unaffected.
/// </summary>
public sealed class MissingProviderCredentialMiddleware(RequestDelegate next)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public async Task InvokeAsync(HttpContext context, HeaderApiKeySnapshot apiKeys)
    {
        try
        {
            await next(context);
        }
        catch (ModelProviderNotFoundException exception) when (ShouldHandle(context, exception, apiKeys, out var headerName))
        {
            if (context.Response.HasStarted)
                throw;

            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.Headers.WWWAuthenticate = "Bearer";

            var body = new
            {
                error = new
                {
                    message = $"You didn't provide an API key. You need to provide your API key in an Authorization header using Bearer auth (i.e. Authorization: Bearer YOUR_KEY), or in the provider-specific {headerName} header.",
                    type = "invalid_request_error",
                    param = (string?)null,
                    code = (string?)null
                }
            };

            await context.Response.WriteAsJsonAsync(body, JsonOptions, context.RequestAborted);
        }
    }

    private static bool ShouldHandle(
        HttpContext context,
        ModelProviderNotFoundException exception,
        HeaderApiKeySnapshot apiKeys,
        out string headerName)
    {
        headerName = string.Empty;

        if (!IsCompatibleInferenceRoute(context.Request.Path))
            return false;

        var split = exception.Model.SplitModelId();
        if (string.IsNullOrWhiteSpace(split.Provider)
            || string.IsNullOrWhiteSpace(split.Model)
            || !HeaderApiKeyResolver.SupportedProviderHeaders.TryGetValue(split.Provider, out headerName))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(apiKeys.Resolve(split.Provider));
    }

    private static bool IsCompatibleInferenceRoute(PathString path)
    {
        if (path.StartsWithSegments("/v1/messages")
            || path.StartsWithSegments("/v1/models")
            || path.StartsWithSegments("/v1/skills"))
        {
            return false;
        }

        return path.StartsWithSegments("/v1")
            || (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/callbacks"));
    }
}
