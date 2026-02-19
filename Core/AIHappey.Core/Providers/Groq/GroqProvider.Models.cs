using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Groq;

public partial class GroqProvider
{

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {

        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        var response = await _client.GetAsync("openai/v1/models", cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        return [.. data
            .EnumerateArray()
            .Select(m =>
            {
                var id = m.GetProperty("id").GetString() ?? string.Empty;
                var created = m.TryGetProperty("created", out var c) ? c.GetInt64() : 0;
                var ownedBy = m.TryGetProperty("owned_by", out var o) ? o.GetString() : string.Empty;

                return new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = id,
                    OwnedBy = ownedBy!,
                    Created = created
                };
            })
            .OrderByDescending(r => r.Created)
            .DistinctBy(r => r.Id)];
    }

}