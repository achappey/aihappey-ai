using AIHappey.Core.AI;
using AIHappey.Core.Models;
using OpenAI.Models;
using OAI = OpenAI;
using OpenAI.Containers;
using OpenAI.Files;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly IApiKeyResolver _keyResolver;

    public OpenAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.openai.com/");
    }

    private string GetKey()
    {
        var key = _keyResolver.Resolve(Constants.OpenAI);

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(OpenAI)} API key.");

        return key;
    }

    private OpenAIFileClient GetFileClient() => new(GetKey());

    private ContainerClient GetContainerClient() => new(GetKey());

    public string GetIdentifier() => Constants.OpenAI;

    private static OAI.Chat.ChatCompletionOptions ToChatCompletionOptions(string model) => new();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var client = new OpenAIModelClient(GetKey());

        var models = await client.GetModelsAsync(cancellationToken);

        return models.Value
            .Where(a => !DeprecatedModels.Contains(a.Id))
            .ToModels();
    }    

    private readonly IEnumerable<string> DeprecatedModels = [
        "davinci-002",
        "codex-mini-latest",
        "babbage-002",
        "dall-e-2",
        "dall-e-3"
     ];
}