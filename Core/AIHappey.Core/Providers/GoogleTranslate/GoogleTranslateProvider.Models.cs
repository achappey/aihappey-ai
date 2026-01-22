using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.GoogleTranslate;

public sealed partial class GoogleTranslateProvider
{
    private const string TranslationV2BaseUrl = "https://translation.googleapis.com/language/translate/v2";

    private sealed class SupportedLanguagesResponse
    {
        public SupportedLanguagesData? Data { get; set; }
    }

    private sealed class SupportedLanguagesData
    {
        public List<SupportedLanguage>? Languages { get; set; }
    }

    private sealed class SupportedLanguage
    {
        public string? Language { get; set; }
        public string? Name { get; set; }
    }

    private async Task<IEnumerable<Model>> ListTranslationModelsAsync(CancellationToken cancellationToken)
    {
        var key = GetKeyOrThrow();

        // Request localized names so the UI can show something friendly.
        // docs: GET https://translation.googleapis.com/language/translate/v2/languages?target=en&key=...
        var url = $"{TranslationV2BaseUrl}/languages?target=en&key={Uri.EscapeDataString(key)}";

        using var resp = await _client.GetAsync(url, cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"GoogleTranslate languages failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<SupportedLanguagesResponse>(body, JsonSerializerOptions.Web);
        var langs = parsed?.Data?.Languages ?? [];

        var models = langs
            .Where(l => !string.IsNullOrWhiteSpace(l.Language))
            .Select(l =>
            {
                var languageCode = l.Language!.Trim();
                var name = l.Name?.Trim();
                var display = string.IsNullOrWhiteSpace(name) ? languageCode : name;

                return new Model
                {
                    OwnedBy = "Google Translate",
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

