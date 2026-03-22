using AIHappey.Core.Models;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.TextSynth;

public partial class TextSynthProvider
{
    private Task<IEnumerable<Model>> ListModelsInternal(CancellationToken cancellationToken = default)
    {
        var staticModels = GetIdentifier().GetModels().ToList();
        var models = new List<Model>(staticModels);

        var speechBaseModels = staticModels
            .Where(m => string.Equals(m.Type, "speech", StringComparison.OrdinalIgnoreCase)
                && string.Equals(ExtractProviderLocalModelId(m.Id), SpeechBaseModel, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var baseModel in speechBaseModels)
        {
            foreach (var voice in TextSynthSpeechVoices)
            {
                models.Add(new Model
                {
                    Id = $"{SpeechBaseModel}/{voice}".ToModelId(GetIdentifier()),
                    OwnedBy = string.IsNullOrWhiteSpace(baseModel.OwnedBy) ? nameof(TextSynth) : baseModel.OwnedBy,
                    Type = "speech",
                    Name = $"{SpeechBaseModel} · {voice}",
                    Description = $"TextSynth text-to-speech voice {voice} on {SpeechBaseModel}.",
                    Tags = [
                        $"model:{SpeechBaseModel}",
                        $"voice:{voice.ToLowerInvariant()}"
                    ]
                });
            }
        }

        return Task.FromResult<IEnumerable<Model>>([.. models
            .GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())]);
    }
}

