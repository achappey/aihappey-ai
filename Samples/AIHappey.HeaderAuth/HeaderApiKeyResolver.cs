using AIHappey.Core.AI;

namespace AIHappey.HeaderAuth;

public class HeaderApiKeyResolver(IHttpContextAccessor http) : IApiKeyResolver
{
    private static readonly Dictionary<string, string> ProviderHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["openai"] = "X-OpenAI-Key",
            ["mistral"] = "X-Mistral-Key",
            ["anthropic"] = "X-Anthropic-Key",
            ["google"] = "X-Google-Key",
            ["perplexity"] = "X-Perplexity-Key",
            ["cohere"] = "X-Cohere-Key",
            ["runway"] = "X-Runway-Key",
            ["aiml"] = "X-AIML-Key",
            ["jina"] = "X-Jina-Key",
            ["xai"] = "X-xAI-Key",
            ["scaleway"] = "X-Scaleway-Key",
            ["nscale"] = "X-Nscale-Key",
            ["sambanova"] = "X-SambaNova-Key",
            ["stabilityai"] = "X-StabilityAI-Key",
            ["groq"] = "X-Groq-Key",
            ["novita"] = "X-Novita-Key",
            ["together"] = "X-Together-Key",
        };

    public string? Resolve(string provider)
    {
        var ctx = http.HttpContext;
        if (ctx == null)
            return null;

        if (!ProviderHeaders.TryGetValue(provider, out var headerName))
            return null;

        // Try canonical name, then lowercase variant
        var headers = ctx.Request.Headers;
        return headers[headerName].FirstOrDefault()
            ?? headers[headerName.ToLowerInvariant()].FirstOrDefault();
    }
}
