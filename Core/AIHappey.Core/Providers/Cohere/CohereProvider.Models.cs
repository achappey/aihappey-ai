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

                var result = new List<Model>();
                var seenPageTokens = new HashSet<string>(StringComparer.Ordinal);
                string? pageToken = null;

                do
                {
                    var url = "v1/models?page_size=1000";
                    if (!string.IsNullOrEmpty(pageToken))
                        url += "&page_token=" + Uri.EscapeDataString(pageToken);

                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    using var response = await _client.SendAsync(request, ct);

                    var payload = await response.Content.ReadAsStringAsync(ct);
                    response.EnsureSuccessStatusCode();

                    using var doc = JsonDocument.Parse(payload);
                    if (doc.RootElement.TryGetProperty("models", out var modelsEl)
                        && modelsEl.ValueKind == JsonValueKind.Array)
                    {
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
                    }

                    pageToken = doc.RootElement.TryGetProperty("next_page_token", out var nextPageTokenEl)
                        && nextPageTokenEl.ValueKind == JsonValueKind.String
                        ? nextPageTokenEl.GetString()
                        : null;
                }
                while (!string.IsNullOrEmpty(pageToken) && seenPageTokens.Add(pageToken));

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
            return "embedding";

        return "language";
    }
}
