using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Tinfoil;

public sealed partial class TinfoilProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Tinfoil API error: {payload}");

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();

        foreach (var item in data.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var ownedBy = item.TryGetProperty("owned_by", out var ownedEl) ? ownedEl.GetString() : null;

            long? created = null;
            if (item.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == JsonValueKind.Number
                && createdEl.TryGetInt64(out var epoch))
            {
                created = epoch;
            }

            int? contextWindow = null;
            if (item.TryGetProperty("context_window", out var cwEl) && cwEl.ValueKind == JsonValueKind.Number
                && cwEl.TryGetInt32(out var cw))
            {
                contextWindow = cw;
            }

            int? maxTokens = null;
            if (item.TryGetProperty("max_tokens", out var mtEl) && mtEl.ValueKind == JsonValueKind.Number
                && mtEl.TryGetInt32(out var mt))
            {
                maxTokens = mt;
            }

            var type = item.TryGetProperty("type", out var typeEl) && typeEl.ValueKind == JsonValueKind.String
                ? typeEl.GetString()
                : null;

            type = type switch
            {
                "chat" or "code" or "title" or "safety" => "language",
                "audio" => "transcription",
                _ => type
            };

            models.Add(new Model
            {
                Id = id!.ToModelId(GetIdentifier()),
                Name = id!,
                OwnedBy = ownedBy ?? "tinfoil",
                Created = created,
                ContextWindow = contextWindow,
                MaxTokens = maxTokens,
                Type = type ?? id!.GuessModelType()
            });
        }

        return models
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .Where(m => m.Type != "document" && m.Type != "tool")
            .DistinctBy(m => m.Id)
            .OrderByDescending(m => m.Created);
    }
}

