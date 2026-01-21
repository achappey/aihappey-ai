using AIHappey.Core.AI;
using AIHappey.Core.Models;
using Azure;
using Azure.AI.Translation.Text;

namespace AIHappey.Core.Providers.Azure;

public sealed partial class AzureProvider
{
    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(_endpoint))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var credential = new AzureKeyCredential(key);

        var client = new TextTranslationClient(credential,
            region: GetEndpointRegion(),
            new TextTranslationClientOptions()
            {

            });

        var languageModels = await client.GetSupportedLanguagesAsync(cancellationToken: cancellationToken);
        var langModels = languageModels.Value.Translation
            .Select(a => new Model()
            {
                Name = "Translate to " + a.Value.Name,
                Description = a.Value.NativeName,
                OwnedBy = nameof(Azure),
                Id = ("translate-to-" + a.Key).ToModelId(GetIdentifier()),
                Type = "language",
            });

        return await Task.FromResult<IEnumerable<Model>>([
            new Model
            {
                OwnedBy = nameof(Azure),
                Name = "speech-to-text",
                Type = "transcription",
                Id = "speech-to-text".ToModelId(GetIdentifier())
            },
            new Model
            {
                OwnedBy = nameof(Azure),
                Name = "text-to-speech",
                Type = "speech",
                Id = "text-to-speech".ToModelId(GetIdentifier())
            },
            ..langModels
        ]);
    }
}

