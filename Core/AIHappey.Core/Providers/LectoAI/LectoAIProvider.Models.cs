using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.LectoAI;

public sealed partial class LectoAIProvider
{
    private sealed class LanguagesResponse
    {
        [JsonPropertyName("languages")]
        public List<LanguageItem>? Languages { get; set; }
    }

    private sealed class LanguageItem
    {
        [JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("language_code")]
        public string? LanguageCode { get; set; }

        [JsonPropertyName("support_source")]
        public bool? SupportSource { get; set; }

        [JsonPropertyName("support_target")]
        public bool? SupportTarget { get; set; }
    }

    private async Task<IEnumerable<Model>> ListTranslationModelsAsync(CancellationToken cancellationToken)
    {
        // LectoAI docs: GET /v1/translate/languages
        using var resp = await _client.GetAsync("v1/translate/languages", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"LectoAI languages failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<LanguagesResponse>(body, JsonSerializerOptions.Web);
        var langs = parsed?.Languages ?? [];

        var models = langs
            .Where(l => (l.SupportTarget ?? true) && !string.IsNullOrWhiteSpace(l.LanguageCode))
            .Select(l =>
            {
                var languageCode = l.LanguageCode!.Trim();
                var display = string.IsNullOrWhiteSpace(l.DisplayName) ? languageCode : l.DisplayName!.Trim();

                return new Model
                {
                    OwnedBy = "LectoAI",
                    Type = "language",
                    Id = $"translate/{languageCode}".ToModelId(GetIdentifier()),
                    Name = $"Translate to {display}",
                    Description = languageCode,
                };
            })
            .ToList();

        return models;
    }
}

