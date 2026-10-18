using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.VercelAIGateway;

public partial class VercelAIGatewayProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = ApplyAuthHeader();

        return await _memoryCache.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var response = await _client.GetAsync("v1/models", ct);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    throw new InvalidOperationException($"Vercel AI Gateway model discovery failed: {error}");
                }

                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

                var models = new List<Model>();
                if (!document.RootElement.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Array)
                    return models;

                foreach (var element in data.EnumerateArray())
                {
                    if (!element.TryGetProperty("id", out var idElement)
                        || idElement.ValueKind != JsonValueKind.String
                        || string.IsNullOrWhiteSpace(idElement.GetString()))
                        continue;

                    var nativeId = idElement.GetString()!;
                    var model = JsonSerializer.Deserialize<Model>(element.GetRawText(), JsonSerializerOptions.Web)
                        ?? new Model();

                    if (element.TryGetProperty("type", out var typeElement)
                        && typeElement.ValueKind == JsonValueKind.String
                        && !IsChatModelType(typeElement.GetString()))
                        continue;

                    if (!element.TryGetProperty("type", out _)
                        && nativeId.GuessModelType() != "language")
                        continue;

                    model.Id = nativeId.ToModelId(GetIdentifier());
                    model.Name = string.IsNullOrWhiteSpace(model.Name) ? nativeId : model.Name;
                    model.Type = string.IsNullOrWhiteSpace(model.Type) ? "language" : model.Type;
                    models.Add(model);
                }

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static bool IsChatModelType(string? type)
        => string.IsNullOrWhiteSpace(type)
           || type.Equals("language", StringComparison.OrdinalIgnoreCase)
           || type.Equals("text-generation", StringComparison.OrdinalIgnoreCase)
           || type.Equals("chat", StringComparison.OrdinalIgnoreCase);
}
