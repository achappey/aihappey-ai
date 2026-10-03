using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.M8tes;

public partial class M8tesProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) return [];

        // Freeze both values before entering the cache factory or issuing any page requests.
        var userId = _userResolver.Resolve(new ChatRequest());
        if (string.IsNullOrWhiteSpace(userId)) userId = null;
        var cacheIdentity = JsonSerializer.Serialize(new[] { key, userId });
        var cacheKey = $"{GetIdentifier()}:agents:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(cacheIdentity)))}";

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            ct => ListM8tesAgentsAsync(key, userId, ct),
            baseTtl: TimeSpan.FromHours(1), jitterMinutes: 30, cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> ListM8tesAgentsAsync(string key, string? userId,
        CancellationToken cancellationToken)
    {
        var models = new List<Model>();
        var seenIds = new HashSet<long>();
        var seenCursors = new HashSet<long>();
        long? startingAfter = null;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = "agents/?limit=100";
            if (startingAfter is not null)
                path += $"&starting_after={startingAfter.Value.ToString(CultureInfo.InvariantCulture)}";
            // The local OpenAPI agents-list schema explicitly permits this query parameter.
            if (userId is not null)
                path += $"&user_id={Uri.EscapeDataString(userId)}";

            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array
                || !root.TryGetProperty("has_more", out var hasMore)
                || hasMore.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new JsonException("Invalid M8tes agents-list response.");

            long? lastId = null;
            foreach (var agent in data.EnumerateArray())
            {
                if (agent.ValueKind != JsonValueKind.Object
                    || !agent.TryGetProperty("id", out var idElement)
                    || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt64(out var id))
                    throw new JsonException("Invalid M8tes agent ID.");

                lastId = id;
                if (!seenIds.Add(id)) continue;
                var idText = id.ToString(CultureInfo.InvariantCulture);
                var name = agent.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
                    ? nameElement.GetString() : null;
                long? created = agent.TryGetProperty("created_at", out var createdElement)
                    && createdElement.ValueKind == JsonValueKind.String && createdElement.TryGetDateTimeOffset(out var createdAt)
                        ? createdAt.ToUnixTimeSeconds() : null;
                models.Add(new Model
                {
                    Id = idText.ToModelId(GetIdentifier()),
                    Name = string.IsNullOrWhiteSpace(name) ? idText : name,
                    OwnedBy = "M8tes",
                    Type = "chat",
                    Created = created,
                    Description = $"M8tes agent '{name ?? idText}' using its configured model and tools.",
                    Tags = ["agent"]
                });
            }

            if (!hasMore.GetBoolean()) return models;
            // starting_after is the last item ID, not the optional response cursor alias.
            if (lastId is null || !seenCursors.Add(lastId.Value))
                throw new JsonException("M8tes agents pagination did not advance.");
            startingAfter = lastId;
        }
    }
}
