using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Volink;

public partial class VolinkProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                using var response = await _client.SendAsync(request, ct);
                var raw = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Volink model discovery failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}",
                        null,
                        response.StatusCode);

                try
                {
                    using var document = JsonDocument.Parse(raw);
                    if (!document.RootElement.TryGetProperty("data", out var data)
                        || data.ValueKind != JsonValueKind.Array)
                        throw new JsonException("The Volink model response must contain a data array.");

                    return data.EnumerateArray()
                        .Select(ReadModel)
                        .Where(model => model is not null)
                        .Cast<Model>()
                        .ToArray();
                }
                catch (JsonException exception)
                {
                    throw new InvalidOperationException("Volink returned an invalid model list.", exception);
                }
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
        var type = ReadString(item, "type")?.ToLowerInvariant() ?? id.GuessModelType();
        if (string.Equals(type, "decisions", StringComparison.OrdinalIgnoreCase))
            type = "decision";

        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = ReadString(item, "name") ?? id,
            OwnedBy = ReadString(item, "owned_by") ?? GetIdentifier(),
            Type = type,
            Created = ReadInt64(item, "created"),
            Description = ReadString(item, "description"),
            ContextWindow = ReadInt32(item, "context_window"),
            MaxTokens = ReadInt32(item, "max_tokens")
                ?? ReadInt32(item, "max_completion_tokens"),
            Tags = ReadStringArray(item, "tags")
        };
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? ReadInt32(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number)
                ? number
                : null;

    private static long? ReadInt64(JsonElement element, string property)
        => element.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var number)
                ? number
                : null;

    private static IEnumerable<string>? ReadStringArray(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .ToArray()
            : null;
}
