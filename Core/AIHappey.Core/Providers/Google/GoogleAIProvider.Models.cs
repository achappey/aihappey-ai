using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync(
            cacheKey,
            async ct =>
            {
                var googleAI = GetClient();
                var generativeModel = googleAI.GenerativeModel();
                var models = await generativeModel.ListModels(pageSize: 1000);

                string[] excludedSubstrings = [
                    "embedding",
                    "native",
                ];

                var rawModels = models
                    .Select(a =>
                    {
                        var id = a.Name?.Split("/").LastOrDefault() ?? string.Empty;

                        GoogleAIModels.ModelCreatedAt.TryGetValue(id, out var createdAt);

                        return new Model()
                        {
                            Name = a.DisplayName!,
                            OwnedBy = Google,
                            Description = id,
                            Id = id.ToModelId(GetIdentifier()),
                            Type = id.GuessModelType(),
                            Created = createdAt != default ? createdAt.ToUnixTimeSeconds() : null
                        };
                    })
                    .Where(a => excludedSubstrings.All(z => a.Id?.Contains(z) != true))
                    .ToList();

                rawModels.AddRange(BuildGoogleSpeechVoiceShortcutModels(rawModels.ToList(), GetIdentifier()));

                var transcriptionModel = rawModels.FirstOrDefault(a => a.Id.EndsWith("gemini-3.5-flash"));

                if (transcriptionModel != null)
                {
                    rawModels.Add(new Model()
                    {
                        Name = transcriptionModel.Name,
                        Id = transcriptionModel.Id,
                        OwnedBy = transcriptionModel.OwnedBy,
                        Description = transcriptionModel.Description,
                        Created = transcriptionModel.Created,
                        Type = "transcription"
                    });
                }

                return rawModels
                    .WithPricing(GetIdentifier());

            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private static IEnumerable<Model> BuildGoogleSpeechVoiceShortcutModels(IEnumerable<Model> models, string providerId)
    {
        var existingIds = new HashSet<string>(
            models.Select(model => model.Id),
            StringComparer.OrdinalIgnoreCase);

        foreach (var baseModel in models)
        {
            var baseModelId = GetLocalGoogleModelId(baseModel, providerId);
            if (string.IsNullOrWhiteSpace(baseModelId)
                || !baseModelId.Contains("tts", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var voice in GoogleSpeechVoices)
            {
                var shortcutId = $"{baseModelId}/{voice.Name}";
                var providerModelId = shortcutId.ToModelId(providerId);
                if (!existingIds.Add(providerModelId))
                    continue;

                yield return new Model
                {
                    Id = providerModelId,
                    Name = shortcutId,
                    OwnedBy = Google,
                    Type = "speech",
                    Created = baseModel.Created,
                    Description = $"Google Gemini text-to-speech model '{baseModelId}' with preset voice '{voice.Name}' ({voice.Style}).",
                    Tags = ["tts", "speech", $"model:{baseModelId}", $"voice:{voice.Name}", $"voice-style:{voice.Style}", "shortcut"]
                };
            }
        }
    }

    private static string GetLocalGoogleModelId(Model model, string providerId)
    {
        var id = model.Id ?? string.Empty;
        var providerPrefix = providerId + "/";
        if (id.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return id[providerPrefix.Length..];

        return id;
    }
}
