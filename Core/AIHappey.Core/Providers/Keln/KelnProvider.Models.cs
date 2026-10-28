using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Keln;

public sealed partial class KelnProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _cache.GetOrCreateAsync(this.GetCacheKey(_keyResolver.Resolve(GetIdentifier())), async ct =>
        {
            using var response = await _client.GetAsync("v1/models", ct);
            var raw = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Keln models request failed: {raw}", null, response.StatusCode);
            using var document = JsonDocument.Parse(raw);
            var models = new List<Model>();
            foreach (var row in document.RootElement.GetProperty("data").EnumerateArray())
            {
                var id = row.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id))
                    throw new JsonException("Keln returned a model without an ID.");
                var decision = row.TryGetProperty("output_modalities", out var output)
                    && output.ValueKind == JsonValueKind.Array && output.GetArrayLength() == 1
                    && output[0].GetString() == "decisions";
                var modalities = row.TryGetProperty("input_modalities", out var input) && input.ValueKind == JsonValueKind.Array
                    ? input.EnumerateArray().Select(v => v.GetString()!).ToArray() : [];
                models.Add(new Model
                {
                    Id = id.ToModelId(GetIdentifier()),
                    Name = Text(row, "name") ?? id,
                    OwnedBy = Text(row, "owned_by") ?? "Keln",
                    Description = Text(row, "description"),
                    ContextWindow = Number(row, "context_window") ?? Number(row, "context_length"),
                    MaxTokens = Number(row, "max_output_tokens") ?? Number(row, "max_tokens"),
                    Created = row.TryGetProperty("created", out var created) && created.TryGetInt64(out var timestamp) ? timestamp : null,
                    Type = decision ? "decision" : "language",
                    Tags = modalities.Length > 0 ? modalities : null
                });
            }
            return models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 30, cancellationToken: cancellationToken);
    }

    private static string? Text(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int? Number(JsonElement row, string name)
        => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : null;
}
