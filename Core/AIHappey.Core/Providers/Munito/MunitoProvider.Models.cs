using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Munito;

public sealed partial class MunitoProvider
{
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
                        $"Munito model discovery failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                        null,
                        response.StatusCode);

                List<Model> models;
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The Munito model response must contain a data array.");

                    models = data.EnumerateArray().Select(ReadModel).OfType<Model>().ToList();
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("Munito returned an invalid model list.", exception);
                }

                // The live list has no model type; the catalog supplies decision, OCR and transcription types.
                foreach (var catalogModel in GetIdentifier().GetModels())
                {
                    var live = models.FirstOrDefault(model =>
                        string.Equals(model.Id, catalogModel.Id, StringComparison.OrdinalIgnoreCase));
                    if (live is null)
                    {
                        models.Add(catalogModel);
                        continue;
                    }

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
            Name = id,
            OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString() ?? GetIdentifier()
                : GetIdentifier(),
            Created = item.TryGetProperty("created", out var created) && created.TryGetInt64(out var value)
                ? value
                : null,
            Type = IsOcrModel(id) ? "language" : id.GuessModelType()
        };
    }
}
