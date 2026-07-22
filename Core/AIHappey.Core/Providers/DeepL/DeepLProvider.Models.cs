using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
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

                using var resp = await _client.GetAsync("v2/languages?type=target", cancellationToken);
                var body = await resp.Content.ReadAsStringAsync(cancellationToken);

                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"{nameof(DeepL)} languages failed ({(int)resp.StatusCode}): {body}");

                var languages = JsonSerializer.Deserialize<List<DeepLLanguage>>(body, JsonSerializerOptions.Web) ?? [];

                var models = languages
                    .Where(l => !string.IsNullOrWhiteSpace(l.Language))
                    .Select(l =>
                    {
                        var code = l.Language!.Trim();
                        var display = string.IsNullOrWhiteSpace(l.Name) ? code : l.Name!.Trim();

                        return new Model
                        {
                            OwnedBy = nameof(DeepL),
                            Type = "language",
                            Tags = ["translate", code.NormalizeLanguageCode()],
                            Id = $"translate-to/{code}".ToModelId(GetIdentifier()),
                            Name = $"{nameof(DeepL)} Translate to {display}",
                            Description = code
                        };
                    })
                    .ToList();

                models.Add(new Model()
                {
                    OwnedBy = nameof(DeepL),
                    Type = "language",
                    Id = $"rephrase".ToModelId(GetIdentifier()),
                    Name = $"Improve text"
                });

                return models;
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }
}
