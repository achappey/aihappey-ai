using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.UncloseAI;

public partial class UncloseAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var cacheKey = this.GetCacheKey();

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var results = await Task.WhenAll(
                    FetchModelsAsync(HermesRoute, HermesBaseUri, ct),
                    FetchModelsAsync(QwenRoute, QwenBaseUri, ct));

                return results
                    .SelectMany(static x => x)
                    .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private async Task<IReadOnlyList<Model>> FetchModelsAsync(string route, Uri baseUri, CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildUrl(baseUri, "v1/models"));
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"UncloseAI API error for route '{route}': {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var payload = await JsonSerializer.DeserializeAsync<UncloseAiModelsResponse>(stream, cancellationToken: cancellationToken);

        return payload?.Data?
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Id))
            .Select(entry => new Model
            {
                Id = $"{route}/{entry.Id}".ToModelId(GetIdentifier()),
                Name = entry.Id!,
                OwnedBy = entry.OwnedBy ?? string.Empty
            })
            .ToArray() ?? [];
    }
}
