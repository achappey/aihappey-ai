using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Responses;
using AIHappey.Common.Model.ChatCompletions;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public NovitaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.novita.ai/openai/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Novita)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Novita).ToLowerInvariant();


    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var modelId = options.Model ?? throw new ArgumentException(options.Model);
        var models = StaticModels(GetIdentifier());
        var model = models.FirstOrDefault(a => a.Id.EndsWith(modelId));

        if (model != null)
        {

            if (model.Type == "speech")
            {
                return await this.SpeechResponseAsync(options, cancellationToken);
            }
        }

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}