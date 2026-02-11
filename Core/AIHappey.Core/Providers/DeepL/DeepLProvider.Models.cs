using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.DeepL;

public partial class DeepLProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken)
    {

        // Avoid throwing during model discovery when the key is not configured.
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();

        using var resp = await _client.GetAsync("v2/languages?type=target", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepL languages failed ({(int)resp.StatusCode}): {body}");

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
                    Id = $"translate-to/{code}".ToModelId(GetIdentifier()),
                    Name = $"DeepL Translate to {display}",
                    Description = code
                };
            })
            .ToList();

        return models;
    }
}
