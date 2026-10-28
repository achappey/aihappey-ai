using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Leonardo;

public sealed partial class LeonardoProvider
{
    private async Task<IEnumerable<Model>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = CreateRequest(HttpMethod.Get, "platformModels", key);
                using var response = await _client.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Leonardo model discovery failed ({(int)response.StatusCode}): {content}",
                        null,
                        response.StatusCode);

                using var document = JsonDocument.Parse(content);
                if (!TryGetProperty(document.RootElement, "custom_models", out var modelsElement)
                    || modelsElement.ValueKind != JsonValueKind.Array)
                    return [];

                return modelsElement.EnumerateArray()
                    .Select(ToModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
            return null;

        var id = GetString(item, "id");
        if (string.IsNullOrWhiteSpace(id))
            return null;
       

        return new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = GetString(item, "name") ?? id,
            OwnedBy = GetIdentifier(),
            Description = GetString(item, "description"),
            Type = "image"
        };
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string key)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? GetString(JsonElement element, string propertyName)
        => TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}
