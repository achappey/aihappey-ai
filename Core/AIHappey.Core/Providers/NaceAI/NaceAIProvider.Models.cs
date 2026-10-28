using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NaceAI;

public sealed partial class NaceAIProvider
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
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var response = await _client.SendAsync(request, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"NaceAI model discovery failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                        null,
                        response.StatusCode);

                List<Model> models;
                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (!document.RootElement.TryGetProperty("models", out var data) || data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The NaceAI model response must contain a models array.");

                    models = data.EnumerateArray().Select(ReadModel).OfType<Model>().ToList();
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("NaceAI returned an invalid model list.", exception);
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
            || !item.TryGetProperty("name", out var nameElement)
            || nameElement.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(nameElement.GetString()))
            return null;

        var description = item.TryGetProperty("description", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()
            : null;
        // Retired versions are only served through their alias target, so they are not listed.
        if (description?.StartsWith("Retired", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        var name = nameElement.GetString()!;
        return new Model
        {
            Id = name.ToModelId(GetIdentifier()),
            Name = name,
            Description = description,
            OwnedBy = GetIdentifier(),
            Type = "decision",
            ContextWindow = 131072,
            Created = item.TryGetProperty("release_date", out var release)
                      && release.ValueKind == JsonValueKind.String
                      && DateTimeOffset.TryParse(release.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                          System.Globalization.DateTimeStyles.AssumeUniversal, out var date)
                ? date.ToUnixTimeSeconds()
                : null
        };
    }
}
