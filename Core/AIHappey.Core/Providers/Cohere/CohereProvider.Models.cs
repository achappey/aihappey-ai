using AIHappey.Core.AI;
using AIHappey.Core.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider
{
    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                ApplyAuthHeader();

                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?page_size=1000");
                using var response = await _client.SendAsync(request, cancellationToken);

                var payload = await response.Content.ReadAsStringAsync(cancellationToken);
                response.EnsureSuccessStatusCode();

                using var doc = JsonDocument.Parse(payload);
                if (!doc.RootElement.TryGetProperty("models", out var modelsEl)
                    || modelsEl.ValueKind != JsonValueKind.Array)
                    return [];

                var result = new List<Model>();

                foreach (var item in modelsEl.EnumerateArray())
                {
                    var name = item.TryGetProperty("name", out var nameEl)
                        ? nameEl.GetString()
                        : null;

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    int? contextLength = null;
                    if (item.TryGetProperty("context_length", out var contextLengthEl))
                        contextLength = contextLengthEl.GetInt32();

                    IEnumerable<string>? tags = null;
                    if (item.TryGetProperty("features", out var featuresEl)
                        && featuresEl.ValueKind == JsonValueKind.Array)
                    {
                        tags = [.. featuresEl
                    .EnumerateArray()
                    .Where(f => f.ValueKind == JsonValueKind.String)
                    .Select(f => f.GetString()!)
                    .Where(s => !string.IsNullOrWhiteSpace(s))];
                    }

                    var modelType = ResolveModelType(name!, tags);

                    if (!string.IsNullOrEmpty(modelType))
                        result.Add(new Model
                        {
                            Id = name!.ToModelId(GetIdentifier()),
                            Name = name!,
                            ContextWindow = contextLength,
                            OwnedBy = nameof(Cohere),
                            Type = modelType
                        });
                }

                return result.WithPricing(GetIdentifier());
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);

    }

    private static string? ResolveModelType(string name, IEnumerable<string>? tags)
    {
        if (name.Contains("transcribe", StringComparison.OrdinalIgnoreCase)
            || tags?.Any(t => t.Contains("transcription", StringComparison.OrdinalIgnoreCase)
                || t.Contains("speech-to-text", StringComparison.OrdinalIgnoreCase)
                || t.Contains("audio-transcription", StringComparison.OrdinalIgnoreCase)) == true)
        {
            return "transcription";
        }

        if (name?.Contains("rerank", StringComparison.OrdinalIgnoreCase) == true)
            return "reranking";

        if (name?.Contains("embed", StringComparison.OrdinalIgnoreCase) == true)
            return null;

        return "language";
    }
}
