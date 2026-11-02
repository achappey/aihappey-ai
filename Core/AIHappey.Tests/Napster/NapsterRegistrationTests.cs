using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Napster;
using AIHappey.HeaderAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace AIHappey.Tests.Napster;

public sealed class NapsterRegistrationTests
{
    [Fact]
    public void HeaderCredentialsAndProviderRegistrationUseExistingInfrastructure()
    {
        Assert.Equal("X-Napster-Key", HeaderApiKeyResolver.SupportedProviderHeaders["napster"]);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Napster-Key"] = "fixture-server-key";
        var resolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("fixture-server-key", resolver.Resolve("napster"));
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IApiKeyResolver>(resolver);
        services.AddProviders();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("napster"));
        Assert.IsType<NapsterProvider>(registry.GetModelProvider(scope.ServiceProvider, "napster"));

        var handler = root.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("napster-api");
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Fact]
    public async Task SelectionAliasesDoNotAdvertiseIncompatibleGenericEndpoints()
    {
        var provider = new NapsterProvider(new Keys(), new Factory());
        var models = (await provider.ListModels()).ToArray();
        Assert.Equal(["napster/agent", "napster/companion"], models.Select(m => m.Id).ToArray());
        Assert.All(models, model =>
        {
            Assert.Contains("real-time", model.Tags!);
            Assert.DoesNotContain("realtime-token", model.Tags!);
            Assert.DoesNotContain("realtime-session", model.Tags!);
        });
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeTokenAsync("agent", new()));
        await Assert.ThrowsAsync<NotSupportedException>(() => provider.CreateRealtimeSessionAsync("agent", new() { Sdp = "offer" }));
    }

    private sealed class Keys : IApiKeyResolver { public string? Resolve(string provider) => null; }
    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NoHttp());
    }
    private sealed class NoHttp : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected HTTP request.");
    }
}
