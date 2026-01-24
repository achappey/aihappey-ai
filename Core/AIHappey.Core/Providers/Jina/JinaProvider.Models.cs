using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Jina;

public partial class JinaProvider
{
    public async Task<IEnumerable<Model>> ListModels(
      CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var owner = nameof(Jina);

        return await Task.FromResult<IEnumerable<Model>>(
        [
        new Model
        {
            Id = "jina-deepsearch-v1".ToModelId(GetIdentifier()),
            Name = "jina-deepsearch-v1",
            OwnedBy = owner,
            Created = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-v3".ToModelId(GetIdentifier()),
            Name = "jina-reranker-v3",
            OwnedBy = owner,
            Created = new DateTimeOffset(2025, 10, 1, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-m0".ToModelId(GetIdentifier()),
            Name = "jina-reranker-m0",
            OwnedBy = owner,
            Created = new DateTimeOffset(2025, 4, 8, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-colbert-v2".ToModelId(GetIdentifier()),
            Name = "jina-colbert-v2",
            OwnedBy = owner,
            Type = "reranking",
            Created = new DateTimeOffset(2024, 8, 31, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-v2-base-multilingual".ToModelId(GetIdentifier()),
            Name = "jina-reranker-v2-base-multilingual",
            OwnedBy = owner,
            Created = new DateTimeOffset(2024, 6, 25, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-v1-tiny-en".ToModelId(GetIdentifier()),
            Name = "jina-reranker-v1-tiny-en",
            OwnedBy = owner,
            Created = new DateTimeOffset(2024, 4, 18, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-v1-turbo-en".ToModelId(GetIdentifier()),
            Name = "jina-reranker-v1-turbo-en",
            OwnedBy = owner,
            Created = new DateTimeOffset(2024, 4, 18, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-reranker-v1-base-en".ToModelId(GetIdentifier()),
            Name = "jina-reranker-v1-base-en",
            OwnedBy = owner,
            Created = new DateTimeOffset(2024, 2, 29, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        },

        new Model
        {
            Id = "jina-colbert-v1-en".ToModelId(GetIdentifier()),
            Name = "jina-colbert-v1-en",
            OwnedBy = owner,
            Type = "reranking",
            Created = new DateTimeOffset(2024, 2, 17, 0, 0, 0, TimeSpan.Zero)
                .ToUnixTimeSeconds()
        }
    ]);
    }

}