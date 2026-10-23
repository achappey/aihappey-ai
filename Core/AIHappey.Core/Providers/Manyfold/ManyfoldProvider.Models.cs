using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Manyfold;

public partial class ManyfoldProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Manyfold API key.");

        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            using var request = CreateRequest(HttpMethod.Get, "api/agents", key);
            using var response = await _client.SendAsync(request, ct);
            await EnsureSuccessAsync(response, ct);
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Manyfold returned an invalid agent list; expected an array.");

            return (IEnumerable<Model>)document.RootElement.EnumerateArray()
                .Where(agent => !string.IsNullOrWhiteSpace(Text(agent, "id")))
                .Select(agent => new Model
                {
                    Id = $"manyfold/{Text(agent, "id")}",
                    Name = Text(agent, "name") ?? Text(agent, "id")!,
                    OwnedBy = "Manyfold",
                    Type = "language",
                    Tags = ["agent"],
                    Created = DateTimeOffset.TryParse(Text(agent, "createdAt"), out var created)
                        ? created.ToUnixTimeSeconds() : null,
                    Description = Text(agent, "description"),
                    Metadata = new Dictionary<string, JsonElement> { ["manyfold"] = agent.Clone() }
                }).ToList();
        }, TimeSpan.FromMinutes(5), jitterMinutes: 1, cancellationToken);
    }
}
