using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Clarifai;

public sealed partial class ClarifaiProvider
{
    private async Task<IEnumerable<Model>> ListClarifaiModelsAsync(CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                var models = new List<Model>();
                for (var page = 1; ; page++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"v2/models?page={page}");
                    request.Headers.Authorization = new("Key", key);

                    using var response = await _modelClient.SendAsync(request, ct);
                    if (!response.IsSuccessStatusCode)
                    {
                        var error = await response.Content.ReadAsStringAsync(ct);
                        throw new HttpRequestException(
                            $"Clarifai model discovery failed with status {(int)response.StatusCode}: {error}",
                            null,
                            response.StatusCode);
                    }

                    await using var stream = await response.Content.ReadAsStreamAsync(ct);
                    using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                    if (!document.RootElement.TryGetProperty("models", out var data) ||
                        data.ValueKind != JsonValueKind.Array ||
                        data.GetArrayLength() == 0)
                        break;

                    models.AddRange(data.EnumerateArray()
                        .Select(ToClarifaiModel)
                        .OfType<Model>());
                }

                return (IEnumerable<Model>)models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToClarifaiModel(JsonElement item)
    {
        if (!HasOpenAITransport(item))
            return null;

        var userId = GetString(item, "user_id");
        var appId = GetString(item, "app_id");
        var name = GetString(item, "name");
        var modelType = GetString(item, "model_type_id");
        if (string.IsNullOrWhiteSpace(userId) ||
            string.IsNullOrWhiteSpace(appId) ||
            string.IsNullOrWhiteSpace(name) ||
            string.IsNullOrWhiteSpace(modelType))
            return null;

        var url = $"https://clarifai.com/{userId}/{appId}/models/{name}";
        return new Model
        {
            Id = url.ToModelId(GetIdentifier()),
            Name = name,
            OwnedBy = userId,
            Type = MapModelType(modelType),
            Description = GetString(item, "description"),
            Metadata = item.EnumerateObject()
                .ToDictionary(property => property.Name, property => property.Value.Clone())
        };
    }

    private static bool HasOpenAITransport(JsonElement item)
    {
        if (!item.TryGetProperty("model_version", out var version) ||
            !version.TryGetProperty("method_signatures", out var methods) ||
            methods.ValueKind != JsonValueKind.Array)
            return false;

        var methodNames = methods.EnumerateArray()
            .Select(method => GetString(method, "name"))
            .Where(name => name is not null)
            .ToHashSet(StringComparer.Ordinal);

        return methodNames.Contains("openai_transport") &&
               methodNames.Contains("openai_stream_transport");
    }

    private static string MapModelType(string modelType)
        => modelType.ToLowerInvariant() switch
        {
            "text-to-text" => "language",
            "text-to-image" => "image",
            "text-embedder" => "embedding",
            _ => modelType
        };

    private static string? GetString(JsonElement item, string propertyName)
        => item.ValueKind == JsonValueKind.Object &&
           item.TryGetProperty(propertyName, out var value) &&
           value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
