using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Lingvanex;

public sealed partial class LingvanexProvider
{
    private sealed class GetLanguagesResponse
    {
        public JsonElement? Err { get; set; }
        public List<Language>? Result { get; set; }
    }

    private sealed class Language
    {
        public string? Full_Code { get; set; }
        public string? EnglishName { get; set; }
        public string? CodeName { get; set; }
        public List<LanguageMode>? Modes { get; set; }
    }

    private sealed class LanguageMode
    {
        public string? Name { get; set; }
        public bool? Value { get; set; }
    }

    private async Task<IEnumerable<Model>> ListTranslationModelsAsync(CancellationToken cancellationToken)
    {
        // docs: GET getLanguages?platform=api&code=en_GB
        using var resp = await _client.GetAsync("getLanguages?platform=api&code=en_GB", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lingvanex getLanguages failed ({(int)resp.StatusCode}): {body}");

        var parsed = JsonSerializer.Deserialize<GetLanguagesResponse>(body, JsonSerializerOptions.Web);

        var langs = parsed?.Result ?? [];

        static bool SupportsTranslation(Language l)
            => l.Modes?.Any(m => string.Equals(m.Name, "Translation", StringComparison.OrdinalIgnoreCase) && m.Value == true) == true;

        var models = langs
            .Where(l => !string.IsNullOrWhiteSpace(l.Full_Code))
            .Where(SupportsTranslation)
            .Select(l =>
            {
                var fullCode = l.Full_Code!.Trim();
                var name = l.EnglishName?.Trim();
                var display = string.IsNullOrWhiteSpace(name) ? fullCode : name;

                return new Model
                {
                    OwnedBy = "Lingvanex",
                    Type = "language",
                    Id = $"translate/{fullCode}".ToModelId(GetIdentifier()),
                    Name = $"Translate to {display}",
                    Description = l.CodeName
                };
            })
            .ToList();

        return models;
    }
}

