using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Orchestration;

public sealed class ModelProviderResolverTests : IDisposable
{
    private readonly List<IDisposable> _serviceScopes = [];

    public void Dispose()
    {
        foreach (var scope in _serviceScopes.AsEnumerable().Reverse())
            scope.Dispose();
    }

    [Fact]
    public async Task ResolveModels_HeaderAuthWithoutExplicitProviderHeaders_ReturnsOnlyAlwaysIncludeProviders()
    {
        var providers = new[]
        {
            new TestModelProvider("public", "public/model"),
            new TestModelProvider("other", "other/model")
        };
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>()),
            providers,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["public/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["public"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
    }

    [Fact]
    public async Task ResolveModels_HeaderAuthWithSingleExplicitProviderHeader_ReturnsOnlyThatProvider()
    {
        var providers = new[]
        {
            new TestModelProvider("public", "public/model"),
            new TestModelProvider("keyed", "keyed/model"),
            new TestModelProvider("other", "other/model")
        };
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["keyed"] = "request-key"
            }),
            providers,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["keyed/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["keyed"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
    }

    [Fact]
    public async Task ResolveModels_HeaderAuthWithMultipleExplicitProviderHeaders_ReturnsOnlyKeyedProviders()
    {
        var providers = new[]
        {
            new TestModelProvider("public", "public/model"),
            new TestModelProvider("alpha", "alpha/model"),
            new TestModelProvider("beta", "beta/model"),
            new TestModelProvider("other", "other/model")
        };
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["alpha"] = "alpha-key",
                ["beta"] = "beta-key"
            }),
            providers,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["alpha/model", "beta/model"], response.Data.Select(model => model.Id).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["alpha", "beta"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveModels_HeaderAuthBearerOnlyDoesNotCountAsModelListingProviderKey()
    {
        var providers = new[]
        {
            new TestModelProvider("public", "public/model"),
            new TestModelProvider("bearer", "bearer/model")
        };
        var resolver = CreateResolver(
            new BearerOnlyApiKeyResolver("bearer", "bearer-key"),
            providers,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["public/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["public"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
        Assert.Equal("bearer-key", resolver.GetProvider().GetIdentifier() == "bearer" ? "bearer-key" : null);
    }

    [Fact]
    public async Task ResolveModels_ServerSideResolverWithoutPresenceExtensionKeepsConfiguredProviderDiscovery()
    {
        var providers = new[]
        {
            new TestModelProvider("configured", "configured/model"),
            new TestModelProvider("unconfigured", "unconfigured/model")
        };
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>
            {
                ["configured"] = "server-key"
            }),
            providers,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["configured/model", "unconfigured/model"], response.Data.Select(model => model.Id).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(["configured", "unconfigured"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveModels_ServerSideResolverWithPresenceExtensionReturnsOnlyConfiguredProviders()
    {
        var providers = new[]
        {
            new TestModelProvider("configured", "configured/model"),
            new TestModelProvider("empty", "empty/model"),
            new TestModelProvider("unconfigured", "unconfigured/model")
        };
        var resolver = CreateResolver(
            new ConfigPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["configured"] = "server-key",
                ["empty"] = " "
            }),
            providers);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["configured/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["configured"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
    }

    [Fact]
    public async Task ResolveModels_LiveAggregatePreservesSameIdWithDifferentTypesAndCollapsesExactIdentityDuplicates()
    {
        var provider = new TestModelProvider(
            "multi",
            "multi/shared",
            [
                ("multi/shared", "language"),
                ("multi/shared", "image"),
                ("MULTI/SHARED", "IMAGE")
            ]);
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [provider]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(2, response.Data.Count());
        Assert.Contains(response.Data, model => model.Id == "multi/shared" && model.Type == "language");
        Assert.Contains(response.Data, model => string.Equals(model.Id, "multi/shared", StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Resolve_DisabledModelThrowsButResolveModelsStillIncludesModel()
    {
        var providers = new[]
        {
            new TestModelProvider("openai", "openai/chat-latest")
        };
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            providers,
            disabledModels: ["openai/chat-latest"]);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.Resolve("openai/chat-latest"));
        Assert.Equal("The system administrator has disabled use for the model 'openai/chat-latest'.", exception.Message);

        var response = await resolver.ResolveModels(CancellationToken.None);
        Assert.Equal(["openai/chat-latest"], response.Data.Select(model => model.Id));
    }

    [Fact]
    public async Task Resolve_DisabledResolvedModelThrowsForUnprefixedRequest()
    {
        var providers = new[]
        {
            new TestModelProvider("openai", "openai/chat-latest")
        };
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            providers,
            disabledModels: ["openai/chat-latest"]);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.Resolve("chat-latest"));
        Assert.Equal("The system administrator has disabled use for the model 'chat-latest'.", exception.Message);
    }

    [Fact]
    public async Task ResolveModels_BaguetteBot_agents_are_never_shared()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var keys = new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["baguettebot"] = "key" });
        var firstProvider = new TestModelProvider("baguettebot", "baguettebot/agent/first");
        var secondProvider = new TestModelProvider("baguettebot", "baguettebot/agent/second");
        var first = CreateResolver(keys, [firstProvider],
            memoryCache: cache);
        var second = CreateResolver(keys, [secondProvider],
            memoryCache: cache);

        Assert.Equal(["baguettebot/agent/first"], (await first.ResolveModels(CancellationToken.None)).Data.Select(model => model.Id));
        Assert.Equal(["baguettebot/agent/second"], (await second.ResolveModels(CancellationToken.None)).Data.Select(model => model.Id));
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => second.Resolve("baguettebot/agent/first"));
    }

    [Fact]
    public async Task ResolveModels_M8tesScopesWithSameCredentialAndSharedMemoryCacheRemainIsolated()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var keys = new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>
        {
            ["shared"] = "shared-key",
            ["m8tes"] = "same-credential"
        });
        var sharedA = new TestModelProvider("shared", "shared/model");
        var sharedB = new TestModelProvider("shared", "shared/model");
        var scopeA = new TestModelProvider("m8tes", "m8tes/scope-a");
        var scopeB = new TestModelProvider("m8tes", "m8tes/scope-b");
        var resolverA = CreateResolver(keys, [sharedA, scopeA],
            memoryCache: cache);
        var resolverB = CreateResolver(keys, [sharedB, scopeB],
            memoryCache: cache);

        var responseA = await resolverA.ResolveModels(CancellationToken.None);
        var responseB = await resolverB.ResolveModels(CancellationToken.None);
        var responseAAgain = await resolverA.ResolveModels(CancellationToken.None);

        Assert.Equal(["m8tes/scope-a", "shared/model"], responseA.Data.Select(model => model.Id).Order());
        Assert.Equal(["m8tes/scope-b", "shared/model"], responseB.Data.Select(model => model.Id).Order());
        Assert.Equal(["m8tes/scope-a", "shared/model"], responseAAgain.Data.Select(model => model.Id).Order());
        Assert.Same(scopeB, await resolverB.Resolve("scope-b"));
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => resolverB.Resolve("m8tes/scope-a"));
        Assert.Equal(1, sharedA.ListModelsCalls);
        Assert.Equal(0, sharedB.ListModelsCalls);
        Assert.Equal(2, scopeA.ListModelsCalls);
        Assert.Equal(3, scopeB.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_OnlyM8tesKeyedSelectionBypassesSharedMemory()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/current-scope");
        var anonymous = new TestModelProvider("public", "public/model");
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["m8tes"] = "same-credential" }),
            [scoped, anonymous],
            alwaysIncludeProviders: ["public"], memoryCache: new AsyncCacheHelper(memory));

        var first = await resolver.ResolveModels(CancellationToken.None);
        var second = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["m8tes/current-scope"], first.Data.Select(model => model.Id));
        Assert.Equal(["m8tes/current-scope"], second.Data.Select(model => model.Id));
        Assert.Equal(2, scoped.ListModelsCalls);
        Assert.Equal(0, anonymous.ListModelsCalls);
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public async Task ResolveModels_M8tesWithoutSelectedKeyIsNotDiscovered()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/agent");
        var shared = new TestModelProvider("shared", "shared/model");
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["shared"] = "shared-key" }),
            [scoped, shared]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["shared/model"], response.Data.Select(model => model.Id));
        Assert.Equal(0, scoped.ListModelsCalls);
    }

    [Fact]
    public async Task Resolve_M8tesPreservesModelIdentitiesAndDisabledAliasChecks()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/agent",
            [("m8tes/agent", "language"), ("m8tes/agent", "image"), ("M8TES/AGENT", "IMAGE")]);
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [scoped], disabledModels: ["m8tes/agent"]);

        var response = await resolver.ResolveModels(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.Resolve("agent"));

        Assert.Equal(2, response.Data.Count());
        Assert.Contains(response.Data, model => model.Type == "language");
        Assert.Contains(response.Data, model => string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("The system administrator has disabled use for the model 'agent'.", exception.Message);
    }

    [Fact]
    public async Task ResolveModels_ReusesMemoryAcrossRequestsWithSameCredentials()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var keys = new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["shared"] = "key" });
        var providerA = new TestModelProvider("shared", "shared/model");
        var providerB = new TestModelProvider("shared", "shared/should-not-be-discovered");
        var first = CreateResolver(keys, [providerA], memoryCache: cache);
        var second = CreateResolver(keys, [providerB], memoryCache: cache);

        await first.ResolveModels(CancellationToken.None);
        var response = await second.ResolveModels(CancellationToken.None);
        Assert.Equal(["shared/model"], response.Data.Select(model => model.Id));
        Assert.Same(providerB, await second.Resolve("shared/model"));
        Assert.Equal(1, providerA.ListModelsCalls);
        Assert.Equal(0, providerB.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_DifferentCredentialsNeverShareMemory()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var providerA = new TestModelProvider("shared", "shared/account-a");
        var providerB = new TestModelProvider("shared", "shared/account-b");
        var first = CreateResolver(new HeaderPresenceApiKeyResolver(
            new Dictionary<string, string?> { ["shared"] = "key-a" }), [providerA], memoryCache: cache);
        var second = CreateResolver(new HeaderPresenceApiKeyResolver(
            new Dictionary<string, string?> { ["shared"] = "key-b" }), [providerB], memoryCache: cache);

        Assert.Equal(["shared/account-a"], (await first.ResolveModels(CancellationToken.None)).Data.Select(m => m.Id));
        Assert.Equal(["shared/account-b"], (await second.ResolveModels(CancellationToken.None)).Data.Select(m => m.Id));
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => second.Resolve("shared/account-a"));
        Assert.Equal(1, providerA.ListModelsCalls);
        Assert.Equal(1, providerB.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_DifferentProviderSelectionsNeverShareMemory()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var alpha = new TestModelProvider("alpha", "alpha/model");
        var beta = new TestModelProvider("beta", "beta/model");
        var first = CreateResolver(new HeaderPresenceApiKeyResolver(
            new Dictionary<string, string?> { ["alpha"] = "key" }), [alpha, beta], memoryCache: cache);
        var second = CreateResolver(new HeaderPresenceApiKeyResolver(
            new Dictionary<string, string?> { ["beta"] = "key" }), [alpha, beta], memoryCache: cache);

        Assert.Equal(["alpha/model"], (await first.ResolveModels(CancellationToken.None)).Data.Select(m => m.Id));
        Assert.Equal(["beta/model"], (await second.ResolveModels(CancellationToken.None)).Data.Select(m => m.Id));
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => second.Resolve("alpha/model"));
        Assert.Equal(1, alpha.ListModelsCalls);
        Assert.Equal(1, beta.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_ExpiredMemoryIsRebuiltByNextRequest()
    {
        var provider = new TestModelProvider("shared", "shared/model");
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [provider], memoryCacheTtl: TimeSpan.FromMilliseconds(20));

        await resolver.ResolveModels(CancellationToken.None);
        await Task.Delay(100);
        Assert.Equal(1, provider.ListModelsCalls);
        await resolver.ResolveModels(CancellationToken.None);
        Assert.Equal(2, provider.ListModelsCalls);
    }

    [Fact]
    public async Task Resolve_QualifiedColdRequestDiscoversOnlySelectedProvider()
    {
        var selected = new TestModelProvider("selected", "selected/nested/model");
        var other = new TestModelProvider("other", "other/model");
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [selected, other]);

        Assert.Same(selected, await resolver.Resolve("SELECTED/NESTED/MODEL"));
        Assert.Equal(1, selected.ListModelsCalls);
        Assert.Equal(0, other.ListModelsCalls);
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => resolver.Resolve("selected/missing"));
        Assert.Equal(0, other.ListModelsCalls);
    }

    [Fact]
    public async Task Resolve_UnqualifiedRequestUsesAggregateAndMissingModelThrows()
    {
        var provider = new TestModelProvider("shared", "shared/model");
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [provider]);

        Assert.Same(provider, await resolver.Resolve("model"));
        await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => resolver.Resolve("missing"));
        Assert.Equal(1, provider.ListModelsCalls);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("m8tes")]
    public async Task Resolve_QualifiedDiscoveryPreservesProviderFailureBehavior(string providerId)
    {
        var provider = new TestModelProvider(providerId, $"{providerId}/model")
        {
            ListModelsOverride = _ => throw new HttpRequestException("upstream unavailable")
        };
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [provider]);

        if (providerId == "m8tes")
            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.Resolve($"{providerId}/model"));
        else
            await Assert.ThrowsAsync<ModelProviderNotFoundException>(() => resolver.Resolve($"{providerId}/model"));
    }

    [Fact]
    public async Task ResolveModels_NoSelectedProvidersReturnsEmptyWithoutCaching()
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var provider = new TestModelProvider("private", "private/model");
        var resolver = CreateResolver(new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>()),
            [provider], memoryCache: new AsyncCacheHelper(memory));

        Assert.Empty((await resolver.ResolveModels(CancellationToken.None)).Data);
        Assert.Equal(0, provider.ListModelsCalls);
        Assert.Equal(0, memory.Count);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("m8tes")]
    public async Task ResolveModels_ProviderFailureDoesNotHideOtherProviders(string failingProviderId)
    {
        var broken = new TestModelProvider(failingProviderId, "unused")
        {
            ListModelsOverride = _ => throw new HttpRequestException("upstream unavailable")
        };
        var healthy = new TestModelProvider("healthy", "healthy/model");
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [broken, healthy]);

        Assert.Equal(["healthy/model"], (await resolver.ResolveModels(CancellationToken.None)).Data.Select(m => m.Id));
        Assert.Equal(1, broken.ListModelsCalls);
        Assert.Equal(1, healthy.ListModelsCalls);
    }

    [Theory]
    [InlineData("shared")]
    [InlineData("m8tes")]
    public async Task ResolveModels_CancellationDuringDiscoveryPropagatesAndDoesNotCacheFailure(string providerId)
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new TestModelProvider(providerId, $"{providerId}/model")
        {
            ListModelsOverride = token =>
            {
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.FromResult<IEnumerable<Model>>([]);
            }
        };
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [provider]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveModels(cancellation.Token));
        provider.ListModelsOverride = null;
        Assert.Single((await resolver.ResolveModels(CancellationToken.None)).Data);
        Assert.Equal(2, provider.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_CancelledRequestDoesNotServeCachedModels()
    {
        var provider = new TestModelProvider("shared", "shared/model");
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [provider]);
        await resolver.ResolveModels(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.ResolveModels(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.Resolve("shared/model", cancellation.Token));
        Assert.Equal(1, provider.ListModelsCalls);
    }

    [Fact]
    public async Task ResolveModels_PreservesMetadataEnrichmentTypeInferenceAndCreatedOrdering()
    {
        var source = new Model { Id = "source/common", Type = "language", ContextWindow = 32000, MaxTokens = 4096, Created = 10 };
        var target = new Model { Id = "target/common", Type = " " };
        var newest = new Model { Id = "target/newest", Type = "image", Created = 20 };
        var sourceProvider = new TestModelProvider("source", source.Id)
        {
            ListModelsOverride = _ => Task.FromResult<IEnumerable<Model>>([source])
        };
        var targetProvider = new TestModelProvider("target", target.Id)
        {
            ListModelsOverride = _ => Task.FromResult<IEnumerable<Model>>([target, newest])
        };
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()), [sourceProvider, targetProvider]);

        var response = await resolver.ResolveModels(CancellationToken.None);
        Assert.Equal("target/newest", response.Data.First().Id);
        Assert.Equal(source.ContextWindow, target.ContextWindow);
        Assert.Equal(source.MaxTokens, target.MaxTokens);
        Assert.Equal(source.Created, target.Created);
        Assert.False(string.IsNullOrWhiteSpace(target.Type));
    }

    [Fact]
    public async Task ResolveModels_RespectsDiscoveryParallelismLimit()
    {
        var active = 0;
        var atLimit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var discovered = Enumerable.Range(0, 5).Select(index => new TestModelProvider($"p{index}", $"p{index}/model")
        {
            ListModelsOverride = async token =>
            {
                var count = Interlocked.Increment(ref active);
                try
                {
                    Assert.InRange(count, 1, 2);
                    if (count == 2)
                        atLimit.TrySetResult();
                    await release.Task.WaitAsync(token);
                    return [new Model { Id = $"p{index}/model", Type = "language" }];
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        }).ToArray();
        var resolver = CreateResolver(new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            discovered, maxParallelFirstLoad: 2);

        var listing = resolver.ResolveModels(CancellationToken.None);
        try
        {
            await atLimit.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, Volatile.Read(ref active));
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.Equal(5, (await listing).Data.Count());
    }

    private ModelProviderResolver CreateResolver(
        IApiKeyResolver apiKeyResolver,
        IReadOnlyCollection<TestModelProvider> providers,
        string[]? alwaysIncludeProviders = null,
        string[]? disabledModels = null,
        AsyncCacheHelper? memoryCache = null,
        TimeSpan? memoryCacheTtl = null,
        int maxParallelFirstLoad = 8)
    {
        var services = new ServiceCollection();
        var registry = new ProviderRegistry(
            providers.ToDictionary(provider => provider.GetIdentifier(), provider => provider.GetType(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Type>());
        foreach (var provider in providers)
            services.AddKeyedSingleton<IModelProvider>(provider.GetIdentifier().ToLowerInvariant(), provider);

        if (memoryCache == null)
        {
            var memory = new MemoryCache(new MemoryCacheOptions());
            _serviceScopes.Add(memory);
            memoryCache = new AsyncCacheHelper(memory);
        }
        services.AddScoped(serviceProvider => new ModelProviderResolver(
            apiKeyResolver,
            registry,
            serviceProvider,
            memoryCache,
            Options.Create(new ModelListingOptions
            {
                AlwaysIncludeProviders = alwaysIncludeProviders ?? [],
                MemoryCacheTtl = memoryCacheTtl ?? TimeSpan.FromSeconds(5),
                MaxParallelFirstLoad = maxParallelFirstLoad
            }),
            Options.Create(new ModelResolverOptions
            {
                DisabledModels = disabledModels ?? []
            }),
            NullLogger<ModelProviderResolver>.Instance));

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _serviceScopes.Add(serviceProvider);
        var scope = serviceProvider.CreateScope();
        _serviceScopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<ModelProviderResolver>();
    }

    private sealed class HeaderPresenceApiKeyResolver(IReadOnlyDictionary<string, string?> keys) : IApiKeyResolver, IApiKeyPresenceResolver
    {
        public string? Resolve(string provider)
            => keys.TryGetValue(provider, out var key) ? key : null;

        public bool HasConfiguredKey(string provider)
            => !string.IsNullOrWhiteSpace(Resolve(provider));
    }

    private sealed class BearerOnlyApiKeyResolver(string activeProvider, string bearerToken) : IApiKeyResolver, IApiKeyPresenceResolver
    {
        public string? Resolve(string provider)
            => string.Equals(provider, activeProvider, StringComparison.OrdinalIgnoreCase) ? bearerToken : null;

        public bool HasConfiguredKey(string provider) => false;
    }

    private sealed class ServerSideApiKeyResolver(IReadOnlyDictionary<string, string?> keys) : IApiKeyResolver
    {
        public string? Resolve(string provider)
            => keys.TryGetValue(provider, out var key) ? key : null;
    }

    private sealed class ConfigPresenceApiKeyResolver(IReadOnlyDictionary<string, string?> keys) : IApiKeyResolver, IApiKeyPresenceResolver
    {
        public string? Resolve(string provider)
            => keys.TryGetValue(provider, out var key) ? key : null;

        public bool HasConfiguredKey(string provider)
            => !string.IsNullOrWhiteSpace(Resolve(provider));
    }

    private sealed class TestModelProvider(
        string identifier,
        string modelId,
        IReadOnlyList<(string Id, string Type)>? listedModels = null) : IModelProvider
    {
        public int ListModelsCalls { get; private set; }

        public Func<CancellationToken, Task<IEnumerable<Model>>>? ListModelsOverride { get; set; }

        public string GetIdentifier() => identifier;

        public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        {
            ListModelsCalls++;
            cancellationToken.ThrowIfCancellationRequested();
            if (ListModelsOverride != null)
                return ListModelsOverride(cancellationToken);
            return Task.FromResult<IEnumerable<Model>>((listedModels ?? [(modelId, "chat")])
                .Select(model => new Model
                {
                    Id = model.Id,
                    Name = model.Id,
                    OwnedBy = identifier,
                    Created = 1,
                    Type = model.Type
                })
                .ToList());
        }

        public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        private static NotSupportedException CreateUnsupportedException()
            => new("This test provider only supports model listing.");

        public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }

        public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        {
            throw new NotImplementedException();
        }
    }
}
