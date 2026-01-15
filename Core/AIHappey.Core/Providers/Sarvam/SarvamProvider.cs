using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Sarvam;

public sealed partial class SarvamProvider : IModelProvider
{

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public SarvamProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.sarvam.ai/");
    }


    private const string ProviderId = "sarvam";

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Sarvam API key.");

        // Sarvam uses a custom header auth.
        _client.DefaultRequestHeaders.Remove("api-subscription-key");
        _client.DefaultRequestHeaders.Add("api-subscription-key", key);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();


    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetChatCompletion(
             options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options, ct: cancellationToken);
    }

    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var modelId = options.Model ?? throw new ArgumentException(options.Model);
        var models = await ListModels(cancellationToken);
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId))
            ?? throw new ArgumentException(modelId);

        if (model.Type == "speech")
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

