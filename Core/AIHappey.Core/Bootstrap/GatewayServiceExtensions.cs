using AIHappey.Core.Contracts;
using AIHappey.Core.Diagnostics;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIHappey.Core.AI;

/// <summary>Host policies intentionally excluded from reusable provider registration.</summary>
public static class GatewayServiceExtensions
{
    public static void AddGatewayProviders(this IServiceCollection services)
    {
        services.AddProviderDebugEvents();
        services.TryAddSingleton<EndUserIdHasher>();
        services.AddMemoryCache();
        services.AddSingleton<AsyncCacheHelper>();
        services.TryAddSingleton<ModelListingRefreshState>();
        services.AddOptions<ModelListingStorageOptions>();
        services.AddOptions<SkillProviderResolverOptions>();
        services.TryAddSingleton<IModelListingSnapshotStore, NullModelListingSnapshotStore>();
        services.TryAddSingleton<IModelListingRefreshQueue, NullModelListingRefreshQueue>();
        services.TryAddScoped(sp =>
        {
            var http = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            return new ProviderRequestOptions
            {
                DustWorkspaceId = http?.Request.Headers["X-Dust-Workspace-Id"].FirstOrDefault(),
                YandexFolderId = http?.Request.Headers["OpenAI-Project"].FirstOrDefault(),
                SunoCallbackUrl = http is null ? null : $"{http.Request.Scheme}://{http.Request.Host}/api/callbacks/suno"
            };
        });
        services.AddProviders();
    }
}
