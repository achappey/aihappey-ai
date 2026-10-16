using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Getimg;

public partial class GetimgProvider
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
                var root = await SendGetimgAsync(HttpMethod.Get, "models", null, ct);
                var items = root.ValueKind == JsonValueKind.Array
                    ? root
                    : root.TryGetProperty("data", out var data) ? data : default;

                if (items.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return [.. items.EnumerateArray()
                    .Select(ToGetimgModel)
                    .OfType<Model>()];
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToGetimgModel(JsonElement item)
    {
        var id = GetGetimgString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var type = GetGetimgString(item, "type");
        long? created = DateTimeOffset.TryParse(GetGetimgString(item, "created_at"), out var createdAt)
            ? createdAt.ToUnixTimeSeconds()
            : null;

        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetGetimgString(item, "name") ?? id,
            OwnedBy = GetGetimgString(item, "provider") ?? GetGetimgString(item, "owned_by") ?? GetIdentifier(),
            Description = GetGetimgString(item, "description"),
            Type = string.IsNullOrWhiteSpace(type) ? id.GuessModelType() : type.ToLowerInvariant(),
            Created = created,
            Tags = GetGetimgModelTags(item)
        };
    }

    private static List<string>? GetGetimgModelTags(JsonElement item)
    {
        var tags = new List<string>();

        if (item.TryGetProperty("supported_references", out var references) && references.ValueKind == JsonValueKind.Object)
            tags.AddRange(references.EnumerateObject().Select(r => r.Name));

        if (item.TryGetProperty("supports_sound", out var sound) && sound.ValueKind == JsonValueKind.True)
            tags.Add("audio");

        return tags.Count > 0 ? tags : null;
    }
}
