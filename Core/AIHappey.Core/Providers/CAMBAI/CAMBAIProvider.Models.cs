using System.Text.Json;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.CAMBAI;

public partial class CAMBAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        ApplyAuthHeader();

        var models = new List<Model>(await this.ListModels(key));

        var sourceLanguages = await GetSourceLanguagesAsync(cancellationToken);
        var targetLanguages = await GetTargetLanguagesAsync(cancellationToken);

        models.AddRange(BuildTtsModels(sourceLanguages));
        models.AddRange(BuildTranscriptionModels(sourceLanguages));
        models.AddRange(BuildTranslationModels(sourceLanguages, targetLanguages));
        models.AddRange(BuildTranslatedTtsModels(sourceLanguages, targetLanguages));

        return models;
    }

    private async Task<IReadOnlyList<CambaiLanguage>> GetSourceLanguagesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("source-languages", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} source languages failed ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<List<CambaiLanguage>>(body, JsonSerializerOptions.Web) ?? [];
    }

    private async Task<IReadOnlyList<CambaiLanguage>> GetTargetLanguagesAsync(CancellationToken cancellationToken)
    {
        using var resp = await _client.GetAsync("target-languages", cancellationToken);
        var body = await resp.Content.ReadAsStringAsync(cancellationToken);

        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{nameof(CAMBAI)} target languages failed ({(int)resp.StatusCode}): {body}");

        return JsonSerializer.Deserialize<List<CambaiLanguage>>(body, JsonSerializerOptions.Web) ?? [];
    }

    private IEnumerable<Model> BuildTtsModels(IEnumerable<CambaiLanguage> sourceLanguages)
        => sourceLanguages
            .Where(IsValidLanguage)
            .Select(lang => new Model
            {
                Id = $"tts/source-language/{lang.Id}".ToModelId(GetIdentifier()),
                OwnedBy = nameof(CAMBAI),
                Type = "speech",
                Name = $"Text-to-Speech ({GetDisplayName(lang)})",
                Description = $"Convert text to speech using source language {GetDisplayName(lang)}."
            });

    private IEnumerable<Model> BuildTranscriptionModels(IEnumerable<CambaiLanguage> sourceLanguages)
        => sourceLanguages
            .Where(IsValidLanguage)
            .Select(lang => new Model
            {
                Id = $"transcribe/source-language/{lang.Id}".ToModelId(GetIdentifier()),
                OwnedBy = nameof(CAMBAI),
                Type = "transcription",
                Name = $"Transcribe ({GetDisplayName(lang)})",
                Description = $"Transcribe speech to text in {GetDisplayName(lang)}."
            });

    private IEnumerable<Model> BuildTranslationModels(
        IEnumerable<CambaiLanguage> sourceLanguages,
        IEnumerable<CambaiLanguage> targetLanguages)
    {
        var sources = sourceLanguages.Where(IsValidLanguage).ToList();
        var targets = targetLanguages.Where(IsValidLanguage).ToList();

        foreach (var source in sources)
        {
            foreach (var target in targets)
            {
                if (source.Id == target.Id)
                    continue;

                yield return new Model
                {
                    Id = $"translate/source-language/{source.Id}/target-language/{target.Id}".ToModelId(GetIdentifier()),
                    OwnedBy = nameof(CAMBAI),
                    Type = "language",
                    Name = $"Translate {GetDisplayName(source)} to {GetDisplayName(target)}",
                    Description = $"Translate text from {GetDisplayName(source)} to {GetDisplayName(target)}."
                };
            }
        }
    }

    private IEnumerable<Model> BuildTranslatedTtsModels(
        IEnumerable<CambaiLanguage> sourceLanguages,
        IEnumerable<CambaiLanguage> targetLanguages)
    {
        var sources = sourceLanguages.Where(IsValidLanguage).ToList();
        var targets = targetLanguages.Where(IsValidLanguage).ToList();

        foreach (var source in sources)
        {
            foreach (var target in targets)
            {
                if (source.Id == target.Id)
                    continue;

                yield return new Model
                {
                    Id = $"translated-tts/source-language/{source.Id}/target-language/{target.Id}".ToModelId(GetIdentifier()),
                    OwnedBy = nameof(CAMBAI),
                    Type = "speech",
                    Name = $"Translated TTS {GetDisplayName(source)} to {GetDisplayName(target)}",
                    Description = $"Translate and synthesize speech from {GetDisplayName(source)} to {GetDisplayName(target)}."
                };
            }
        }
    }

    private static bool IsValidLanguage(CambaiLanguage lang)
        => lang.Id > 0;

    private static string GetDisplayName(CambaiLanguage lang)
    {
        if (!string.IsNullOrWhiteSpace(lang.Language))
            return lang.Language.Trim();

        if (!string.IsNullOrWhiteSpace(lang.ShortName))
            return lang.ShortName.Trim();

        return lang.Id.ToString();
    }

    private sealed class CambaiLanguage
    {
        public int Id { get; set; }

        public string? Language { get; set; }

        public string? ShortName { get; set; }
    }
}
