using AIHappey.Core.AI;
using System.Text.Json;
using AIHappey.Core.Models;
using System.Text.Json.Serialization;

namespace AIHappey.Core.Providers.WidnAI;

public partial class WidnAIProvider
{
    private static readonly string[] ChatModels = ["anthill", "sugarloaf", "vesuvius"];

    private sealed class WidnLanguage
    {
        [JsonPropertyName("locale")]
        public string? Locale { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("models")]
        public List<string>? Models { get; set; }
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var models = new List<Model>();

        foreach (var chatModel in ChatModels)
        {
            models.Add(new Model
            {
                Id = chatModel.ToModelId(GetIdentifier()),
                Name = chatModel,
                Type = "language",
                OwnedBy = nameof(WidnAI)
            });
        }

        using var resp = await _client.GetAsync("v1/language", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Widn languages failed ({(int)resp.StatusCode}): {body}");

        var languages = JsonSerializer.Deserialize<List<WidnLanguage>>(body, JsonSerializerOptions.Web) ?? [];

        var translationModels = languages
            .SelectMany(l => l.Models ?? [])
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .Select(m => m.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var translationIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var baseModel in translationModels)
        {
            var supported = languages
                .Where(l => !string.IsNullOrWhiteSpace(l.Locale)
                            && (l.Models?.Any(m => string.Equals(m, baseModel, StringComparison.OrdinalIgnoreCase)) == true))
                .Select(l => new { Code = l.Locale!.Trim(), Name = string.IsNullOrWhiteSpace(l.Name) ? l.Locale!.Trim() : l.Name!.Trim() })
                .ToList();

            for (var i = 0; i < supported.Count; i++)
            {
                for (var j = 0; j < supported.Count; j++)
                {
                    if (i == j || string.Equals(supported[i].Code, supported[j].Code, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var localModelId = $"{baseModel}/translate/{supported[i].Code}/to/{supported[j].Code}";
                    var fullModelId = localModelId.ToModelId(GetIdentifier());
                    if (!translationIds.Add(fullModelId))
                        continue;

                    models.Add(new Model
                    {
                        Id = fullModelId,
                        Name = $"{baseModel} Translate {supported[i].Name} to {supported[j].Name}",
                        Type = "language",
                        OwnedBy = nameof(WidnAI),
                        Description = $"Translate text from {supported[i].Name} ({supported[i].Code}) to {supported[j].Name} ({supported[j].Code}) using {baseModel}."
                    });
                }
            }
        }

        return models;

    }
}
