using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Providers.Zoom;
using AIHappey.HeaderAuth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using static AIHappey.Tests.Zoom.ZoomTestHelpers;

namespace AIHappey.Tests.Zoom;

public sealed class ZoomRegistrationTests
{
    [Fact]
    public async Task Catalog_registers_decision_types_and_special_and_transcription_models_without_HTTP()
    {
        var models = (await NoHttp().ListModels()).ToArray();
        Assert.Equal(6, models.Length);
        Assert.Equal("decision", Assert.Single(models, model => model.Id == "zoom/zoom-decisions").Type);
        Assert.Equal("decision", Assert.Single(models, model => model.Id == "zoom/zoom-decisions-pro").Type);
        Assert.Equal("language", Assert.Single(models, model => model.Id == "zoom/summarizer").Type);
        Assert.Equal("transcription", Assert.Single(models, model => model.Id == "zoom/scribe").Type);
        Assert.Equal("transcription", Assert.Single(models, model => model.Id == "zoom/zoom-scribe-en").Type);
        Assert.Equal("transcription", Assert.Single(models, model => model.Id == "zoom/zoom-scribe-multilingual").Type);
    }

    [Fact]
    public void Header_key_and_provider_registry_resolve_Zoom()
    {
        Assert.Equal("X-Zoom-Key", HeaderApiKeyResolver.SupportedProviderHeaders["zoom"]);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Zoom-Key"] = "zoom-key";
        var keyResolver = new HeaderApiKeyResolver(new HeaderApiKeySnapshot(new HttpContextAccessor { HttpContext = context }));
        Assert.Equal("zoom-key", keyResolver.Resolve("zoom"));

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IApiKeyResolver>(keyResolver);
        services.AddProviders();
        using var root = services.BuildServiceProvider();
        using var scope = root.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<ProviderRegistry>();
        Assert.True(registry.HasModelProvider("zoom"));
        Assert.IsType<ZoomProvider>(registry.GetModelProvider(scope.ServiceProvider, "zoom"));
    }
}
