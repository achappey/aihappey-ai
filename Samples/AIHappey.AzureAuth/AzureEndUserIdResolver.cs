using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;
using AIHappey.AzureAuth.Extensions;

namespace AIHappey.AzureAuth;

/// <summary>
/// AzureAuth strategy: one-way hash of authenticated user object id (oid).
/// </summary>
public sealed class AzureEndUserIdResolver(
    IHttpContextAccessor http,
    EndUserIdHasher hasher) : IEndUserIdResolver
{
    public string? Resolve(ChatRequest chatRequest)
    {
        var oid = http.HttpContext?.GetUserOid();
        return hasher.Hash(oid);
    }
}

