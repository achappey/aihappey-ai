using System.Security.Claims;
using Microsoft.Identity.Web;

namespace AIHappey.AzureAuth.Extensions;

public static class HttpExtensions
{
    private const string AgentNameHeader = "X-Agent-Name";

    public static string? GetUserUpn(this HttpContext context) =>
        context.User.FindFirst(ClaimTypes.Upn)?.Value;

    public static string? GetUserOid(this HttpContext context) =>
        context.User.FindFirst(ClaimConstants.ObjectId)?.Value;

    public static string? GetAgentId(this HttpContext context)
    {
        if (!context.Request.Headers.TryGetValue(AgentNameHeader, out var values))
            return null;

        var agentId = values.ToString();
        return string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim();
    }

   
}
