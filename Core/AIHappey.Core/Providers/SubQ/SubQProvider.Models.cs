using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.SubQ;

public sealed partial class SubQProvider
{
    private const string DecisionModelPrefix = "subq-snap";

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                ApplyAuthHeader();
                using var request = new HttpRequestMessage(HttpMethod.Get, "models");
                using var response = await _client.SendAsync(request, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"SubQ model discovery failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                        null,
                        response.StatusCode);

                List<Model> models;
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The SubQ model response must contain a data array.");

                    models = data.EnumerateArray().Select(ReadModel).OfType<Model>().ToList();
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("SubQ returned an invalid model list.", exception);
                }

                // The live list does not say which endpoint a model serves; the catalog supplies the type and limits.
                foreach (var live in models)
                {
                    var catalogModel = GetIdentifier().GetModels().FirstOrDefault(model =>
                        string.Equals(model.Id, live.Id, StringComparison.OrdinalIgnoreCase));
                    if (catalogModel is null)
                        continue;

                    live.Type = catalogModel.Type ?? live.Type;
                    live.Name = catalogModel.Name ?? live.Name;
                    live.Description = catalogModel.Description ?? live.Description;
                    live.ContextWindow ??= catalogModel.ContextWindow;
                    live.MaxTokens ??= catalogModel.MaxTokens;
                    live.Pricing ??= catalogModel.Pricing;
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ReadModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !item.TryGetProperty("id", out var idElement)
            || idElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(idElement.GetString()))
            return null;

        var id = idElement.GetString()!;
        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString() ?? id
                : id,
            OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString() ?? GetIdentifier()
                : GetIdentifier(),
            Created = item.TryGetProperty("created", out var created) && created.TryGetInt64(out var value) && value > 0
                ? value
                : null,
            Type = IsDecisionModel(id) ? "decision" : "language"
        };
    }

    private static string StripProviderPrefix(string? model)
    {
        var value = model ?? string.Empty;
        return value.StartsWith("subq/", StringComparison.OrdinalIgnoreCase) ? value["subq/".Length..] : value;
    }

    private static bool IsDecisionModel(string? model)
    {
        var id = StripProviderPrefix(model);
        if (string.IsNullOrWhiteSpace(id))
            return false;

        var catalog = "subq".GetModels().FirstOrDefault(item =>
            string.Equals(StripProviderPrefix(item.Id), id, StringComparison.OrdinalIgnoreCase));
        return catalog is not null
            ? string.Equals(catalog.Type, "decision", StringComparison.OrdinalIgnoreCase)
            : id.StartsWith(DecisionModelPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
