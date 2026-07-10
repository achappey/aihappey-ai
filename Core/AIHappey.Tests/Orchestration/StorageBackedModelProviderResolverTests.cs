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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;

namespace AIHappey.Tests.Orchestration;

public sealed class StorageBackedModelProviderResolverTests
{
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

    private static StorageBackedModelProviderResolver CreateResolver(
        IApiKeyResolver apiKeyResolver,
        IReadOnlyCollection<TestModelProvider> providers,
        RecordingSnapshotStore snapshotStore,
        bool includeApiKeysInSnapshotIdentity = true,
        string[]? alwaysIncludeProviders = null,
        string[]? disabledModels = null)
        => new(
            apiKeyResolver,
            providers,
            new TestHttpClientFactory(),
            snapshotStore,
            new RecordingRefreshQueue(),
            new AsyncCacheHelper(new MemoryCache(new MemoryCacheOptions())),
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
            NullLogger<StorageBackedModelProviderResolver>.Instance);

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

        public StoredResolvedModelSnapshot? LatestAggregateSnapshot { get; init; }

        public Task<StoredProviderModelSnapshot?> GetProviderSnapshotAsync(
            string providerId,
            string cacheKey,
            CancellationToken cancellationToken = default)
        {
            ProviderSnapshotReads.Add((providerId, cacheKey));
            return Task.FromResult<StoredProviderModelSnapshot?>(null);
        }

        public Task<StoredProviderModelSnapshot?> GetLatestProviderSnapshotAsync(
            string providerId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StoredProviderModelSnapshot?>(null);

        public Task SaveProviderSnapshotAsync(
            string providerId,
            string cacheKey,
            StoredProviderModelSnapshot snapshot,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<StoredResolvedModelSnapshot?> GetAggregateSnapshotAsync(
            string aggregateKey,
            CancellationToken cancellationToken = default)
            => Task.FromResult<StoredResolvedModelSnapshot?>(null);

        public Task<StoredResolvedModelSnapshot?> GetLatestAggregateSnapshotAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(LatestAggregateSnapshot);

        public Task SaveAggregateSnapshotAsync(
            string aggregateKey,
            StoredResolvedModelSnapshot snapshot,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class RecordingRefreshQueue : IModelListingRefreshQueue
    {
        public bool IsEnabled => false;

        public Task EnqueueAsync(ModelListingRefreshRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<ModelListingQueueMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<ModelListingQueueMessage?>(null);

        public Task DeleteAsync(ModelListingQueueMessage message, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private static StoredResolvedModelSnapshot CreateAggregateSnapshot(IReadOnlyCollection<(string ProviderId, string ModelId)> entries)
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
                    Type = "chat"
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

    private sealed class TestModelProvider(string identifier, string modelId) : IModelProvider
    {
        public int ListModelsCalls { get; private set; }

        public string GetIdentifier() => identifier;

        public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        {
            ListModelsCalls++;
            return Task.FromResult<IEnumerable<Model>>(
            [
                new Model
                {
                    Id = modelId,
                    Name = modelId,
                    OwnedBy = identifier,
                    Created = 1,
                    Type = "chat"
                }
            ]);
        }

        public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default) => throw CreateUnsupportedException();

        private static NotSupportedException CreateUnsupportedException()
            => new("This test provider only supports model listing.");
    }
}
