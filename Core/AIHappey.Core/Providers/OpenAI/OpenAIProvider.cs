using AIHappey.Core.AI;
using AIHappey.Core.Models;
using OpenAI.Models;
using OAI = OpenAI;
using OpenAI.Containers;
using OpenAI.Files;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly IApiKeyResolver _keyResolver;
    private readonly IEndUserIdResolver _endUserIdResolver;

    public OpenAIProvider(
        IApiKeyResolver keyResolver,
        IHttpClientFactory httpClientFactory,
        IEndUserIdResolver endUserIdResolver)
    {
        _keyResolver = keyResolver;
        _endUserIdResolver = endUserIdResolver;
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
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var client = new OpenAIModelClient(GetKey());

        var models = await client.GetModelsAsync(cancellationToken);

        var result = models.Value
            .Where(a => !DeprecatedModels.Contains(a.Id))
            .ToModels();

        return [..result, new Model() {
            Id = "whisper-1/translate".ToModelId(GetIdentifier()),
            Description = "Translate audio to English",
            Name = "whisper-1 Translate to English",
            OwnedBy = nameof(OpenAI),
            Type = "transcription"
            }];
    }

    private readonly IEnumerable<string> DeprecatedModels = [
        "davinci-002",
        "codex-mini-latest",
        "babbage-002",
        "dall-e-2",
        "dall-e-3"
     ];

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

}
