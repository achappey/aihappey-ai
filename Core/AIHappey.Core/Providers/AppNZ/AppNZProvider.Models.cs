using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.AppNZ;

public partial class AppNZProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        // Capture the credentials before entering the cache callback.
        using var client = CreateClient(key);
        return await _memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var raw = await SendJsonAsync(client, HttpMethod.Get, "v1/models", null, ct);
            if (!raw.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("AppNZ returned an invalid model catalog.");

            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            foreach (var item in data.EnumerateArray())
            {
                var model = item.Deserialize<Model>(Json)
                    ?? throw new InvalidOperationException("AppNZ returned an invalid model.");
                if (string.IsNullOrWhiteSpace(model.Id))
                    continue;

                var upstreamId = model.Id;
                model.Id = upstreamId.ToModelId(GetIdentifier());
                model.Name = string.IsNullOrWhiteSpace(model.Name) ? upstreamId : model.Name;
                model.OwnedBy = string.IsNullOrWhiteSpace(model.OwnedBy) ? "AppNZ" : model.OwnedBy;
                model.Type = string.IsNullOrWhiteSpace(model.Type) ? upstreamId.GuessModelType() : model.Type;
                if (upstreamId is "app/auto-image" or "cutedsl-image") model.Type = "image";
                if (upstreamId is "appnz-tts") model.Type = "speech";
                if (upstreamId.Contains("music", StringComparison.OrdinalIgnoreCase)
                    || upstreamId.Contains("sound-effects", StringComparison.OrdinalIgnoreCase))
                    model.Type = "speech";
                models[model.Id] = model;
            }

            foreach (var alias in new[] { "app/auto", "app/auto-code", "app/auto-fast", "app/auto-cheap", "app/auto-reasoning", "app/auto-vision", "app/auto-image" })
            {
                var id = alias.ToModelId(GetIdentifier());
                models.TryAdd(id, new Model
                {
                    Id = id, Name = alias, OwnedBy = "AppNZ",
                    Type = alias == "app/auto-image" ? "image" : "language"
                });
            }

            models["appnz/agent"] = new Model
            {
                Id = "appnz/agent", Name = "AppNZ agent", OwnedBy = "AppNZ", Type = "language",
                Tags = ["agent"],
                Description = "Launch a fresh coding-agent task and wait for completion. Configure source, repo, model and other task options through AppNZ provider metadata."
            };
            return models.Values.ToList();
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 2, cancellationToken: cancellationToken);
    }
}
