using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Coze;

public sealed partial class CozeProvider
{
    private const int DiscoveryPageSize = 20;

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var apiKey = GetApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
            return [];

        return await _asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(apiKey),
            ct => DiscoverModelsAsync(apiKey, ct),
            baseTtl: TimeSpan.FromMinutes(15),
            jitterMinutes: 2,
            cancellationToken: cancellationToken);
    }

    private async Task<IEnumerable<Model>> DiscoverModelsAsync(string apiKey, CancellationToken cancellationToken)
    {
        var models = new List<Model>();

        await foreach (var workspace in ListWorkspacesAsync(apiKey, cancellationToken))
        {
            var workspaceId = GetDiscoveryString(workspace, "id");
            if (string.IsNullOrWhiteSpace(workspaceId))
                continue;

            await foreach (var bot in ListBotsAsync(apiKey, workspaceId, cancellationToken))
            {
                var botId = GetDiscoveryString(bot, "id");
                if (string.IsNullOrWhiteSpace(botId))
                    continue;

                models.Add(new Model
                {
                    Id = $"{GetIdentifier()}/{workspaceId}/{botId}",
                    Name = GetDiscoveryString(bot, "name") ?? botId,
                    Description = GetDiscoveryString(bot, "description"),
                    OwnedBy = GetIdentifier(),
                    Type = "language",
                    Tags = ["agent"]
                });
            }
        }

        return models
            .GroupBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private async IAsyncEnumerable<JsonElement> ListWorkspacesAsync(
        string apiKey,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"v1/workspaces?page_size={DiscoveryPageSize}&page_num={page}",
                apiKey);
            var root = await SendJsonAsync(request, cancellationToken);
            var data = GetObject(root, "data");
            var workspaces = GetArray(data, "workspaces");
            foreach (var workspace in workspaces)
                yield return workspace;

            var total = GetInt64(data, "total_count");
            if (workspaces.Length < DiscoveryPageSize ||
                total is not null && page * DiscoveryPageSize >= total)
                yield break;
        }
    }

    private async IAsyncEnumerable<JsonElement> ListBotsAsync(
        string apiKey,
        string workspaceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var page = 1; ; page++)
        {
            var path =
                $"v1/bots?workspace_id={Uri.EscapeDataString(workspaceId)}" +
                $"&page_size={DiscoveryPageSize}&page_index={page}&publish_status=published_online";
            using var request = CreateRequest(HttpMethod.Get, path, apiKey);
            var root = await SendJsonAsync(request, cancellationToken);
            var data = GetObject(root, "data");
            var bots = GetArray(data, "items");
            foreach (var bot in bots)
                yield return bot;

            var total = GetInt64(data, "total");
            if (bots.Length < DiscoveryPageSize ||
                total is not null && page * DiscoveryPageSize >= total)
                yield break;
        }
    }

    private static JsonElement GetObject(JsonElement parent, string propertyName)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Object
            ? value
            : default;

    private static JsonElement[] GetArray(JsonElement parent, string propertyName)
        => parent.ValueKind == JsonValueKind.Object &&
           parent.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.Clone()).ToArray()
            : [];

    private static string? GetDiscoveryString(JsonElement item, string propertyName)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty(propertyName, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null
        };
    }

    private static long? GetInt64(JsonElement item, string propertyName)
        => item.ValueKind == JsonValueKind.Object &&
           item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.Number &&
           value.TryGetInt64(out var result)
            ? result
            : null;
}
