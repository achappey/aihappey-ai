using AIHappey.Core.AI;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using OpenAI.Models;
using OAI = OpenAI;
using OpenAI.Containers;
using OpenAI.Files;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.OpenAI;

public partial class OpenAIProvider : IModelProvider, ISkillProvider, IUnifiedModelProvider
{
    private readonly HttpClient _client;
    private readonly AsyncCacheHelper _memoryCache;
    private readonly IApiKeyResolver _keyResolver;
    private readonly IEndUserIdResolver _endUserIdResolver;

    public OpenAIProvider(
        IApiKeyResolver keyResolver,
        IHttpClientFactory httpClientFactory,
        AsyncCacheHelper memoryCache,
        IEndUserIdResolver endUserIdResolver)
    {
        _keyResolver = keyResolver;
        _memoryCache = memoryCache;
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


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(OpenAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }



    private OpenAIFileClient GetFileClient() => new(GetKey());

    private ContainerClient GetContainerClient() => new(GetKey());

    public string GetIdentifier() => Constants.OpenAI;

    private static OAI.Chat.ChatCompletionOptions ToChatCompletionOptions(string model) => new();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            return await Task.FromResult<IEnumerable<Model>>([]);

        var cacheKey = this.GetCacheKey(key);

        return await _memoryCache.GetOrCreateAsync<IEnumerable<Model>>(
            cacheKey,
            async ct =>
            {
                var client = new OpenAIModelClient(GetKey());

                var models = await client.GetModelsAsync(cancellationToken);

                var result = models.Value
                    .Where(a => !DeprecatedModels.Contains(a.Id))
                    .ToModels()
                    .ToList()
                    .WithPricing(GetIdentifier());

                return [..result, new Model() {
                    Id = "whisper-1/translate".ToModelId(GetIdentifier()),
                    Description = "Translate audio to English",
                    Name = "whisper-1 Translate to English",
                    OwnedBy = nameof(OpenAI),
                    Type = "transcription"
                    }];
            },
        baseTtl: TimeSpan.FromHours(4),
        jitterMinutes: 480,
        cancellationToken: cancellationToken);
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

   public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }

        yield break;
    }
}
