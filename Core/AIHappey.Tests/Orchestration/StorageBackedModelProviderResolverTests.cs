using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Core.Orchestration;
using AIHappey.Core.Storage;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIHappey.Tests.Orchestration;

public sealed class StorageBackedModelProviderResolverTests : IDisposable
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
        var snapshotStore = new RecordingSnapshotStore();
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>()),
            providers,
            snapshotStore,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["public/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["public"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
        Assert.Equal(["public"], snapshotStore.ProviderSnapshotReads.Select(read => read.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
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
        var snapshotStore = new RecordingSnapshotStore();
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["keyed"] = "request-key"
            }),
            providers,
            snapshotStore,
            alwaysIncludeProviders: ["public"]);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["keyed/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["keyed"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
        Assert.Equal(["keyed"], snapshotStore.ProviderSnapshotReads.Select(read => read.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
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
            new RecordingSnapshotStore(),
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
            new RecordingSnapshotStore(),
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
            new RecordingSnapshotStore(),
            includeApiKeysInSnapshotIdentity: false,
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
        var snapshotStore = new RecordingSnapshotStore();
        var resolver = CreateResolver(
            new ConfigPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["configured"] = "server-key",
                ["empty"] = " "
            }),
            providers,
            snapshotStore,
            includeApiKeysInSnapshotIdentity: false);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["configured/model"], response.Data.Select(model => model.Id));
        Assert.Equal(["configured"], providers.Where(provider => provider.ListModelsCalls > 0).Select(provider => provider.GetIdentifier()));
        Assert.Equal(["configured"], snapshotStore.ProviderSnapshotReads.Select(read => read.ProviderId).Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveModels_ServerSideResolverWithPresenceExtensionFiltersSharedAggregateSnapshot()
    {
        var providers = new[]
        {
            new TestModelProvider("configured", "configured/model"),
            new TestModelProvider("unconfigured", "unconfigured/model")
        };
        var snapshotStore = new RecordingSnapshotStore
        {
            LatestAggregateSnapshot = CreateAggregateSnapshot(
            [
                ("configured", "configured/model"),
                ("unconfigured", "unconfigured/model")
            ])
        };
        var resolver = CreateResolver(
            new ConfigPresenceApiKeyResolver(new Dictionary<string, string?>
            {
                ["configured"] = "server-key"
            }),
            providers,
            snapshotStore,
            includeApiKeysInSnapshotIdentity: false);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["configured/model"], response.Data.Select(model => model.Id));
        Assert.DoesNotContain(response.Data, model => model.Id == "unconfigured/model");
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
            [provider],
            new RecordingSnapshotStore());

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(2, response.Data.Count());
        Assert.Contains(response.Data, model => model.Id == "multi/shared" && model.Type == "language");
        Assert.Contains(response.Data, model => string.Equals(model.Id, "multi/shared", StringComparison.OrdinalIgnoreCase)
            && string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveModels_RestoredAggregatePreservesSameIdWithDifferentTypes()
    {
        var provider = new TestModelProvider("multi", "multi/shared");
        var snapshotStore = new RecordingSnapshotStore
        {
            LatestAggregateSnapshot = CreateAggregateSnapshotWithTypes(
            [
                ("multi", "multi/shared", "language"),
                ("multi", "multi/shared", "image")
            ])
        };
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [provider],
            snapshotStore,
            includeApiKeysInSnapshotIdentity: false);

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(2, response.Data.Count());
        Assert.Equal(["image", "language"], response.Data.Select(model => model.Type).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, provider.ListModelsCalls);
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
            new RecordingSnapshotStore(),
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
            new RecordingSnapshotStore(),
            disabledModels: ["openai/chat-latest"]);

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.Resolve("chat-latest"));
        Assert.Equal("The system administrator has disabled use for the model 'chat-latest'.", exception.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveModels_M8tesScopesWithSameCredentialAndSharedMemoryCacheRemainIsolated(bool includeApiKeysInSnapshotIdentity)
    {
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var cache = new AsyncCacheHelper(memory);
        var store = new RecordingSnapshotStore();
        var keys = new HeaderPresenceApiKeyResolver(new Dictionary<string, string?>
        {
            ["shared"] = "shared-key",
            ["m8tes"] = "same-credential"
        });
        var sharedA = new TestModelProvider("shared", "shared/model");
        var sharedB = new TestModelProvider("shared", "shared/model");
        var scopeA = new TestModelProvider("m8tes", "m8tes/scope-a");
        var scopeB = new TestModelProvider("m8tes", "m8tes/scope-b");
        var resolverA = CreateResolver(keys, [sharedA, scopeA], store,
            includeApiKeysInSnapshotIdentity: includeApiKeysInSnapshotIdentity, memoryCache: cache);
        var resolverB = CreateResolver(keys, [sharedB, scopeB], store,
            includeApiKeysInSnapshotIdentity: includeApiKeysInSnapshotIdentity, memoryCache: cache);

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
        Assert.Single(store.ProviderSnapshotWrites);
        Assert.Single(store.AggregateSnapshotWrites);
        Assert.DoesNotContain(store.ProviderSnapshotReads, read => read.ProviderId == "m8tes");
        AssertSharedSnapshotsExcludeM8tes(store);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveModels_LegacyM8tesAggregateAndProviderSnapshotsAreIgnored(bool includeApiKeysInSnapshotIdentity)
    {
        var legacy = CreateAggregateSnapshot([("shared", "shared/model"), ("m8tes", "m8tes/other-scope")]);
        var legacyState = legacy.Providers.Single(state => state.ProviderId == "m8tes");
        legacyState.RefreshAfterUtc = DateTimeOffset.UtcNow.AddHours(-1);
        var store = new RecordingSnapshotStore
        {
            AggregateSnapshot = legacy,
            LatestAggregateSnapshot = legacy,
            ProviderSnapshot = new StoredProviderModelSnapshot
            {
                ProviderId = "m8tes",
                CacheKey = "models:m8tes",
                Models = [legacy.Entries.Single(entry => entry.ProviderId == "m8tes").Model],
                StoredAtUtc = legacy.StoredAtUtc,
                RefreshAfterUtc = legacy.RefreshAfterUtc,
                ExpiresAtUtc = legacy.ExpiresAtUtc
            }
        };
        var shared = new TestModelProvider("shared", "shared/model");
        var recovered = new TestModelProvider("recovered", "recovered/model");
        var scoped = new TestModelProvider("M8TES", "m8tes/current-scope");
        var queue = new RecordingRefreshQueue();
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?> { ["M8TES"] = "same-credential" }),
            [shared, recovered, scoped], store,
            includeApiKeysInSnapshotIdentity: includeApiKeysInSnapshotIdentity, refreshQueue: queue);

        var response = await resolver.ResolveModels(CancellationToken.None);
        await resolver.RefreshQueuedProviderAsync(new ModelListingRefreshRequest
        {
            ProviderId = "m8tes",
            CacheKey = scoped.GetCacheKey(includeApiKeysInSnapshotIdentity ? "same-credential" : null)
        }, CancellationToken.None);
        // Rebuilding from a shared provider refresh must not preserve legacy scoped agents either.
        await resolver.RefreshQueuedProviderAsync(new ModelListingRefreshRequest
        {
            ProviderId = "shared",
            CacheKey = shared.GetCacheKey(null)
        }, CancellationToken.None);

        Assert.Equal(["m8tes/current-scope", "recovered/model", "shared/model"], response.Data.Select(model => model.Id).Order());
        Assert.Equal(1, scoped.ListModelsCalls);
        Assert.DoesNotContain(store.ProviderSnapshotReads, read => string.Equals(read.ProviderId, "m8tes", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(queue.Requests, request => string.Equals(request.ProviderId, "m8tes", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(store.AggregateSnapshotWrites);
        AssertSharedSnapshotsExcludeM8tes(store);
    }

    [Fact]
    public async Task ResolveModels_BackgroundAggregateRefreshNeverDiscoversOrPersistsM8tes()
    {
        var legacy = CreateAggregateSnapshot([("shared", "shared/model"), ("m8tes", "m8tes/other-scope")]);
        legacy.RefreshAfterUtc = DateTimeOffset.UtcNow.AddHours(-1);
        legacy.Providers.Single(state => state.ProviderId == "m8tes").RefreshAfterUtc = legacy.RefreshAfterUtc;
        var store = new RecordingSnapshotStore { LatestAggregateSnapshot = legacy };
        var queue = new RecordingRefreshQueue();
        var scoped = new TestModelProvider("m8tes", "m8tes/current-scope");
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [new TestModelProvider("shared", "shared/model"), scoped], store,
            includeApiKeysInSnapshotIdentity: false, refreshQueue: queue);

        var response = await resolver.ResolveModels(CancellationToken.None);
        var backgroundSnapshot = await store.AggregateSnapshotSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["m8tes/current-scope", "shared/model"], response.Data.Select(model => model.Id).Order());
        Assert.Equal(1, scoped.ListModelsCalls);
        Assert.Equal(["shared/model"], backgroundSnapshot.Entries.Select(entry => entry.Model.Id));
        Assert.Equal(["shared"], backgroundSnapshot.Providers.Select(state => state.ProviderId));
        Assert.DoesNotContain(queue.Requests, request => request.ProviderId == "m8tes");
    }

    [Fact]
    public async Task ResolveModels_OnlyM8tesKeyedSelectionBypassesSharedSnapshotsAndMemory()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/current-scope");
        var anonymous = new TestModelProvider("public", "public/model");
        var store = new RecordingSnapshotStore
        {
            LatestAggregateSnapshot = CreateAggregateSnapshot([("m8tes", "m8tes/other-scope"), ("public", "public/model")])
        };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["m8tes"] = "same-credential" }),
            [scoped, anonymous], store, includeApiKeysInSnapshotIdentity: false,
            alwaysIncludeProviders: ["public"], memoryCache: new AsyncCacheHelper(memory));

        var first = await resolver.ResolveModels(CancellationToken.None);
        var second = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["m8tes/current-scope"], first.Data.Select(model => model.Id));
        Assert.Equal(["m8tes/current-scope"], second.Data.Select(model => model.Id));
        Assert.Equal(2, scoped.ListModelsCalls);
        Assert.Equal(0, anonymous.ListModelsCalls);
        Assert.Equal(0, memory.Count);
        Assert.Equal(0, store.AggregateSnapshotReads);
        Assert.Empty(store.ProviderSnapshotReads);
        Assert.Empty(store.ProviderSnapshotWrites);
        Assert.Empty(store.AggregateSnapshotWrites);
    }

    [Fact]
    public async Task ResolveModels_M8tesWithoutSelectedKeyIsNotDiscovered()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/agent");
        var shared = new TestModelProvider("shared", "shared/model");
        var resolver = CreateResolver(
            new HeaderPresenceApiKeyResolver(new Dictionary<string, string?> { ["shared"] = "shared-key" }),
            [scoped, shared], new RecordingSnapshotStore());

        var response = await resolver.ResolveModels(CancellationToken.None);

        Assert.Equal(["shared/model"], response.Data.Select(model => model.Id));
        Assert.Equal(0, scoped.ListModelsCalls);
    }

    [Fact]
    public async Task Resolve_M8tesPreservesModelIdentitiesAndDisabledAliasChecks()
    {
        var scoped = new TestModelProvider("m8tes", "m8tes/agent",
            [("m8tes/agent", "language"), ("m8tes/agent", "image"), ("M8TES/AGENT", "IMAGE")]);
        var store = new RecordingSnapshotStore();
        var resolver = CreateResolver(
            new ServerSideApiKeyResolver(new Dictionary<string, string?>()),
            [scoped], store, disabledModels: ["m8tes/agent"]);

        var response = await resolver.ResolveModels(CancellationToken.None);
        var exception = await Assert.ThrowsAsync<NotSupportedException>(() => resolver.Resolve("agent"));

        Assert.Equal(2, response.Data.Count());
        Assert.Contains(response.Data, model => model.Type == "language");
        Assert.Contains(response.Data, model => string.Equals(model.Type, "image", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("The system administrator has disabled use for the model 'agent'.", exception.Message);
        Assert.Empty(store.AggregateSnapshotWrites);
    }

    private static void AssertSharedSnapshotsExcludeM8tes(RecordingSnapshotStore store)
    {
        Assert.DoesNotContain(store.ProviderSnapshotWrites, snapshot => string.Equals(snapshot.ProviderId, "m8tes", StringComparison.OrdinalIgnoreCase));
        Assert.All(store.AggregateSnapshotWrites, snapshot =>
        {
            Assert.DoesNotContain(snapshot.Entries, entry => string.Equals(entry.ProviderId, "m8tes", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(snapshot.Providers, state => string.Equals(state.ProviderId, "m8tes", StringComparison.OrdinalIgnoreCase));
        });
    }

    private StorageBackedModelProviderResolver CreateResolver(
        IApiKeyResolver apiKeyResolver,
        IReadOnlyCollection<TestModelProvider> providers,
        RecordingSnapshotStore snapshotStore,
        bool includeApiKeysInSnapshotIdentity = true,
        string[]? alwaysIncludeProviders = null,
        string[]? disabledModels = null,
        AsyncCacheHelper? memoryCache = null,
        RecordingRefreshQueue? refreshQueue = null)
    {
        var services = new ServiceCollection();
        var registry = new ProviderRegistry(
            providers.ToDictionary(provider => provider.GetIdentifier(), provider => provider.GetType(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, Type>());
        foreach (var provider in providers)
            services.AddKeyedSingleton<IModelProvider>(provider.GetIdentifier().ToLowerInvariant(), provider);

        var refreshState = new ModelListingRefreshState();
        var queue = refreshQueue ?? new RecordingRefreshQueue();
        var cache = memoryCache ?? new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions()));
        services.AddScoped(serviceProvider => new StorageBackedModelProviderResolver(
            apiKeyResolver,
            registry,
            serviceProvider,
            serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            refreshState,
            new TestHttpClientFactory(),
            snapshotStore,
            queue,
            cache,
            Options.Create(new ModelListingStorageOptions
            {
                IncludeApiKeysInSnapshotIdentity = includeApiKeysInSnapshotIdentity,
                AlwaysIncludeProviders = alwaysIncludeProviders ?? [],
                MemoryCacheTtl = TimeSpan.FromSeconds(5),
                AggregateRefreshAfter = TimeSpan.FromHours(1),
                ProviderRefreshAfter = TimeSpan.FromHours(1)
            }),
            Options.Create(new ModelResolverOptions
            {
                DisabledModels = disabledModels ?? []
            }),
            NullLogger<StorageBackedModelProviderResolver>.Instance));

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _serviceScopes.Add(serviceProvider);
        var scope = serviceProvider.CreateScope();
        _serviceScopes.Add(scope);
        return scope.ServiceProvider.GetRequiredService<StorageBackedModelProviderResolver>();
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

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class RecordingSnapshotStore : IModelListingSnapshotStore
    {
        public List<(string ProviderId, string CacheKey)> ProviderSnapshotReads { get; } = [];
        public List<StoredProviderModelSnapshot> ProviderSnapshotWrites { get; } = [];
        public List<StoredResolvedModelSnapshot> AggregateSnapshotWrites { get; } = [];
        public TaskCompletionSource<StoredResolvedModelSnapshot> AggregateSnapshotSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int AggregateSnapshotReads { get; private set; }

        public StoredResolvedModelSnapshot? LatestAggregateSnapshot { get; init; }
        public StoredResolvedModelSnapshot? AggregateSnapshot { get; init; }
        public StoredProviderModelSnapshot? ProviderSnapshot { get; init; }

        public Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
            string providerId,
            string cacheKey,
            CancellationToken cancellationToken = default)
        {
            ProviderSnapshotReads.Add((providerId, cacheKey));
            return Task.FromResult(string.Equals(ProviderSnapshot?.ProviderId, providerId, StringComparison.OrdinalIgnoreCase) ? ProviderSnapshot : null);
        }

        public Task<StoredProviderModelSnapshot?> GetLatestProviderSnapshotAsync(
            string providerId,
            CancellationToken cancellationToken = default)
            => GetProviderSnapshotAsync(providerId, "latest", cancellationToken);

        public Task SaveProviderSnapshotAsync(
            string providerId,
            string cacheKey,
            StoredProviderModelSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            lock (ProviderSnapshotWrites)
                ProviderSnapshotWrites.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
            string aggregateKey,
            CancellationToken cancellationToken = default)
        {
            AggregateSnapshotReads++;
            return Task.FromResult(AggregateSnapshot);
        }

        public Task<StoredResolvedModelSnapshot?> GetLatestAggregateSnapshotAsync(CancellationToken cancellationToken = default)
        {
            AggregateSnapshotReads++;
            return Task.FromResult(LatestAggregateSnapshot);
        }

        public Task SaveAggregateSnapshotAsync(
            string aggregateKey,
            StoredResolvedModelSnapshot snapshot,
            CancellationToken cancellationToken = default)
        {
            lock (AggregateSnapshotWrites)
                AggregateSnapshotWrites.Add(snapshot);
            AggregateSnapshotSaved.TrySetResult(snapshot);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRefreshQueue : IModelListingRefreshQueue
    {
        public bool IsEnabled => false;
        public List<ModelListingRefreshRequest> Requests { get; } = [];

        public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
        {
            lock (Requests)
                Requests.Add(request);
            return Task.CompletedTask;
        }

        public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ModelListingQueueMessage?>(null);

        public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static StoredResolvedModelSnapshot CreateAggregateSnapshot(IReadOnlyCollection<(string ProviderId, string ModelId)> entries)
        => CreateAggregateSnapshotWithTypes([.. entries.Select(entry => (entry.ProviderId, entry.ModelId, "chat"))]);

    private static StoredResolvedModelSnapshot CreateAggregateSnapshotWithTypes(
        IReadOnlyCollection<(string ProviderId, string ModelId, string Type)> entries)
    {
        var now = DateTimeOffset.UtcNow;

        return new StoredResolvedModelSnapshot
        {
            AggregateKey = "resolver:test",
            StoredAtUtc = now,
            RefreshAfterUtc = now.AddHours(1),
            ExpiresAtUtc = now.AddDays(1),
            Entries = [.. entries.Select(entry => new StoredResolvedModelEntry
            {
                ProviderId = entry.ProviderId,
                Model = new Model
                {
                    Id = entry.ModelId,
                    Name = entry.ModelId,
                    OwnedBy = entry.ProviderId,
                    Created = 1,
                    Type = entry.Type
                }
            })],
            Providers = [.. entries.Select(entry => new StoredResolvedProviderState
            {
                ProviderId = entry.ProviderId,
                CacheKey = $"models:{entry.ProviderId}",
                SourceCacheKey = $"models:{entry.ProviderId}",
                StoredAtUtc = now,
                RefreshAfterUtc = now.AddHours(1),
                ExpiresAtUtc = now.AddDays(1)
            })]
        };
    }

    private sealed class TestModelProvider(
        string identifier,
        string modelId,
        IReadOnlyList<(string Id, string Type)>? listedModels = null) : IModelProvider
    {
        public int ListModelsCalls { get; private set; }

        public string GetIdentifier() => identifier;

        public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        {
            ListModelsCalls++;
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
    }
}
