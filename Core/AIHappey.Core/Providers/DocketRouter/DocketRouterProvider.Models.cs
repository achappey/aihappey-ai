using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DocketRouter;

public sealed partial class DocketRouterProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await _cache.GetOrCreateAsync(this.GetCacheKey(), async ct =>
        {
            // Public catalog: do not require or attach a user's inference credential.
            using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
            using var response = await _catalogClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = document.RootElement;
            var hasAllowlist = root.TryGetProperty("callable_models", out var callable) && callable.ValueKind == JsonValueKind.Array;
            var allowlist = hasAllowlist
                ? callable.EnumerateArray().Select(m => m.GetString()).ToHashSet(StringComparer.Ordinal)
                : [];
            List<Model> models = [];
            foreach (var entry in root.GetProperty("data").EnumerateArray())
            {
                var id = entry.GetProperty("id").GetString();
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (entry.TryGetProperty("callable", out var flag) && flag.ValueKind == JsonValueKind.False) continue;
                if (hasAllowlist ? !allowlist.Contains(id) : flag.ValueKind != JsonValueKind.True) continue;

                var model = entry.Deserialize<Model>(JsonSerializerOptions.Web)!;
                model.Id = id.ToModelId(GetIdentifier());
                model.Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name;
                model.OwnedBy ??= "DocketRouter";
                model.Type = "language";
                model.MaxTokens = Math.Min(model.MaxTokens ?? 8192, 8192);
                model.Tags = model.Tags?.Where(t => t is not ("tool-use" or "vision" or "image" or "audio"));
                model.Metadata ??= [];
                model.Metadata["modalities"] = JsonSerializer.SerializeToElement(new { input = new[] { "text" }, output = new[] { "text" } });
                model.Metadata["supported_parameters"] = JsonSerializer.SerializeToElement(new[] { "temperature", "max_tokens", "response_format" });
                models.Add(model);
            }
            return models;
        }, baseTtl: TimeSpan.FromMinutes(15), jitterMinutes: 5, cancellationToken: cancellationToken);
}
