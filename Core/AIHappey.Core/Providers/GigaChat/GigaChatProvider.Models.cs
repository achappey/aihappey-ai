using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.GigaChat;

public sealed partial class GigaChatProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(GetApiKey()))
            return [];

        var accessToken = await GetAccessTokenAsync(cancellationToken);
        using var client = CreateApiClient(accessToken);
        using var response = await client.GetAsync(
            "v1/models",
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"GigaChat model discovery failed with status {(int)response.StatusCode}: {error}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();
        foreach (var item in data.EnumerateArray())
        {
            if (!item.TryGetProperty("id", out var idElement) ||
                idElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(idElement.GetString()) ||
                !item.TryGetProperty("type", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                !string.Equals(typeElement.GetString(), "chat", StringComparison.OrdinalIgnoreCase))
                continue;

            var id = idElement.GetString()!;
            var metadata = item.EnumerateObject()
                .Where(property => property.Name is not ("id" or "object" or "owned_by" or "type"))
                .ToDictionary(property => property.Name, property => property.Value.Clone());

            models.Add(new Model
            {
                Id = $"{GetIdentifier()}/{id}",
                Name = id,
                OwnedBy = item.TryGetProperty("owned_by", out var owner) &&
                          owner.ValueKind == JsonValueKind.String
                    ? owner.GetString() ?? GetIdentifier()
                    : GetIdentifier(),
                Type = "language"
            });
        }

        return models;
    }
}
