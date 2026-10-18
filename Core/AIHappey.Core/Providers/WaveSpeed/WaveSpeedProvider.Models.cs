using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.WaveSpeed;

public partial class WaveSpeedProvider
{
    private async Task<IEnumerable<Model>> ListWaveSpeedModelsAsync(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");

                if (!string.IsNullOrWhiteSpace(key))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

                using var response = await _client.SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new HttpRequestException(
                        $"WaveSpeed model discovery failed with status {(int)response.StatusCode}: {error}",
                        null,
                        response.StatusCode);
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var data = document.RootElement.ValueKind == JsonValueKind.Array
                    ? document.RootElement
                    : document.RootElement.TryGetProperty("data", out var items) &&
                      items.ValueKind == JsonValueKind.Array
                        ? items
                        : default;

                if (data.ValueKind != JsonValueKind.Array)
                    return (IEnumerable<Model>)[];

                return data.EnumerateArray()
                    .Select(ToWaveSpeedModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToWaveSpeedModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var id = idElement.GetString();
        if (string.IsNullOrWhiteSpace(id))
            return null;

        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "name") ?? id,
            OwnedBy = GetString(item, "owned_by") ?? GetIdentifier(),
            Description = GetString(item, "description"),
            Type = "language",
            Metadata = item.EnumerateObject()
                .Where(property => !KnownModelProperties.Contains(property.Name))
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };

        if (item.TryGetProperty("created", out var created) &&
            created.ValueKind == JsonValueKind.Number &&
            created.TryGetInt64(out var timestamp))
        {
            model.Created = timestamp;
        }

        if (item.TryGetProperty("context_window", out var contextWindow) &&
            contextWindow.ValueKind == JsonValueKind.Number &&
            contextWindow.TryGetInt32(out var contextWindowValue))
        {
            model.ContextWindow = contextWindowValue;
        }

        if (item.TryGetProperty("max_tokens", out var maxTokens) &&
            maxTokens.ValueKind == JsonValueKind.Number &&
            maxTokens.TryGetInt32(out var maxTokensValue))
        {
            model.MaxTokens = maxTokensValue;
        }

        return model;
    }

    private static readonly HashSet<string> KnownModelProperties = new(StringComparer.Ordinal)
    {
        "id",
        "object",
        "created",
        "owned_by",
        "name",
        "description",
        "context_window",
        "max_tokens",
        "type",
        "tags",
        "pricing"
    };

    private static string? GetString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
