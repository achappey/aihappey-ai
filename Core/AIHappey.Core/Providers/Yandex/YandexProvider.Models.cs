using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Yandex;

public sealed partial class YandexProvider
{
    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.Ordinal)
    {
        "id", "object", "created", "owned_by"
    };

    private async Task<IEnumerable<Model>> ListYandexModelsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];
        var folder = GetFolderId();
        if (string.IsNullOrWhiteSpace(folder))
            throw new InvalidOperationException(
                "Yandex AI Studio model discovery requires OpenAI-Project or AIServices:Yandex:FolderId.");

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key) + ":" + folder,
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new("Api-Key", key);
                request.Headers.Add("OpenAI-Project", folder);
                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"Yandex AI Studio model discovery failed with status {(int)response.StatusCode}: {error}",
                        null, response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    throw new JsonException("Yandex AI Studio model discovery must contain a data array.");

                return (IEnumerable<Model>)data.EnumerateArray()
                    .Select(ToModel)
                    .OfType<Model>()
                    .DistinctBy(model => model.Id)
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
            return null;

        var id = idElement.GetString()!;
        var isChatUri = id.StartsWith("gpt://", StringComparison.Ordinal);
        var path = isChatUri ? id["gpt://".Length..].Split('/') : [];
        var isTunedEmbedding = path.Length >= 3 && path[1] == "text-embeddings";
        var type = id.StartsWith("emb://", StringComparison.Ordinal) || isTunedEmbedding ? "embedding"
            : isChatUri ? "language" : null;
        if (type is null)
            return null;

        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = id,
            OwnedBy = item.TryGetProperty("owned_by", out var owner) && owner.ValueKind == JsonValueKind.String
                ? owner.GetString()! : null!,
            Type = type
        };

        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number &&
            created.TryGetInt64(out var timestamp))
            model.Created = timestamp;

        return model;
    }
}
