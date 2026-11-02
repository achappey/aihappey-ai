using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Creora;

public sealed partial class CreoraProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];
        return await memoryCache.GetOrCreateAsync(this.GetCacheKey(key), async ct =>
        {
            var result = await SendJsonAsync(HttpMethod.Get, "models", null, ct);
            var models = new List<Model>();
            foreach (var item in result.Root.GetProperty("data").EnumerateArray())
            {
                var model = item.Deserialize<Model>()
                    ?? throw new InvalidOperationException("Creora returned an empty model.");
                var id = model.Id;
                ArgumentException.ThrowIfNullOrWhiteSpace(id);
                model.Id = id.ToModelId(GetIdentifier());
                model.Name = string.IsNullOrWhiteSpace(model.Name) ? id : model.Name;
                model.OwnedBy ??= GetIdentifier();
                var type = model.Type switch
                {
                    "chat" or "text" => "language",
                    null or "" => id switch
                    {
                        "creora-chat" or "creora-network" => "language",
                        "creora-image" => "image",
                        "creora-video" => "video",
                        _ => model.Type
                    },
                    _ => model.Type
                };
                if (type is not null)
                    model.Type = type;
                models.Add(model);
            }
            return (IEnumerable<Model>)models;
        }, baseTtl: TimeSpan.FromHours(4), jitterMinutes: 480, cancellationToken: cancellationToken);
    }
}
