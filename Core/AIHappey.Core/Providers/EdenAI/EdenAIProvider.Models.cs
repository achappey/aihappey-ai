using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.EdenAI;

public partial class EdenAIProvider
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

                var models = new List<Model>();
                foreach (var (path, type) in EdenAIModelEndpoints)
                    models.AddRange(await ListEdenAIModelsAsync(path, type, ct));

                models.AddRange(GetIdentifier().GetModels());

                return models
                    .GroupBy(model => (model.Id, model.Type))
                    .Select(group => group.First())
                    .ToList();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static readonly (string Path, string Type)[] EdenAIModelEndpoints =
    [
        ("v3/models", "language"),
        ("v3/images/models", "image"),
        ("v3/audio/speech/models", "speech"),
        ("v3/audio/transcriptions/models", "transcription")
    ];

    private async Task<IEnumerable<Model>> ListEdenAIModelsAsync(string path, string type, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using var response = await _client.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"EdenAI model listing failed for '{path}' ({(int)response.StatusCode}): {raw}");

        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();
        foreach (var element in data.EnumerateArray())
        {
            var id = ReadEdenAIString(element, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var model = new Model
            {
                Id = id.ToModelId(GetIdentifier()),
                Name = ReadEdenAIString(element, "model_name") ?? id,
                Type = type,
                OwnedBy = ReadEdenAIString(element, "owned_by") ?? string.Empty,
                Description = ReadEdenAIString(element, "description"),
                Created = ReadEdenAIInt64(element, "created"),
                ContextWindow = ReadEdenAIInt32(element, "context_length")
            };

            if (element.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
            {
                var input = ReadEdenAIDecimal(pricing, "input_cost_per_token");
                var output = ReadEdenAIDecimal(pricing, "output_cost_per_token");
                if (input.HasValue || output.HasValue)
                    model.Pricing = new ModelPricing { Input = input ?? 0, Output = output ?? 0 };
            }

            models.Add(model);
        }

        return models;
    }

    private static string? ReadEdenAIString(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long? ReadEdenAIInt64(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt64(out var result) ? result : null;

    private static int? ReadEdenAIInt32(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static decimal? ReadEdenAIDecimal(JsonElement element, string propertyName)
        => element.TryGetProperty(propertyName, out var value) && value.TryGetDecimal(out var result) ? result : null;
}
