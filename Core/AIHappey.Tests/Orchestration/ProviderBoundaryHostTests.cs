using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Orchestration;

public sealed class ProviderBoundaryHostTests
{
    [Fact]
    public void Gateway_snapshots_provider_inputs_per_operation_without_leaking_headers_between_scopes()
    {
        var http = new DefaultHttpContext();
        http.Request.Scheme = "https";
        http.Request.Host = new HostString("gateway.example");
        http.Request.Headers["X-Dust-Workspace-Id"] = "workspace-a";
        http.Request.Headers["OpenAI-Project"] = "folder-a";
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = http });
        services.AddGatewayProviders();
        using var root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        ProviderRequestOptions first;
        using (var scope = root.CreateScope())
        {
            first = scope.ServiceProvider.GetRequiredService<ProviderRequestOptions>();
            Assert.Equal("workspace-a", first.DustWorkspaceId);
            Assert.Equal("folder-a", first.YandexFolderId);
            Assert.Equal("https://gateway.example/api/callbacks/suno", first.SunoCallbackUrl);
            Assert.Same(first, scope.ServiceProvider.GetRequiredService<ProviderRequestOptions>());
        }
        http.Request.Headers.Clear();
        using var nextScope = root.CreateScope();
        var next = nextScope.ServiceProvider.GetRequiredService<ProviderRequestOptions>();
        Assert.Null(next.DustWorkspaceId);
        Assert.Null(next.YandexFolderId);
        Assert.Equal("workspace-a", first.DustWorkspaceId);
        Assert.Equal("folder-a", first.YandexFolderId);
        Assert.NotSame(first, next);
    }
}
