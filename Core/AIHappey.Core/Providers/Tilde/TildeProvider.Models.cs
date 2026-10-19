using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Tilde;

public sealed partial class TildeProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Get, "api/engine", key);
                using var response = await _client.SendAsync(request, ct);
                await EnsureSuccessAsync(response, ct);
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("engines", out var engines) ||
                    engines.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Tilde engine discovery must contain an engines array.");

                return (IEnumerable<Model>)engines.EnumerateArray().Select(ToModel).OfType<Model>()
                    .DistinctBy(model => model.Id).ToArray();
            },
            baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement engine)
    {
        if (engine.ValueKind != JsonValueKind.Object ||
            !Guid.TryParse(GetString(engine, "id"), out var id) || id == Guid.Empty ||
            GetString(engine, "vendor") is not ("Tilde" or "TildeOpen") ||
            GetString(engine, "status") is not ("Running" or "StandBy") ||
            !HasLanguages(engine, "sourceLanguages") || !HasLanguages(engine, "targetLanguages"))
            return null;

        // Keep the complete native object without overwriting the standardized model id/name.
        var metadata = engine.EnumerateObject()
            .Where(property => property.Name is not ("id" or "name"))
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        metadata["engine"] = engine.Clone();
        return new Model
        {
            Id = $"{GetIdentifier()}/{id:D}",
            Name = GetString(engine, "name") ?? id.ToString("D"),
            OwnedBy = GetString(engine, "vendor")!,
            Type = "language",
            Tags = ["translate"],
            Metadata = metadata
        };
    }

    private static bool HasLanguages(JsonElement engine, string property)
        => engine.TryGetProperty(property, out var languages) && languages.ValueKind == JsonValueKind.Array &&
           languages.EnumerateArray().Any(language =>
               language.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(language.GetString()));

    private static string? GetString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
