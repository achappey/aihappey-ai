using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Astica;

public partial class AsticaProvider : IModelProvider
{
    private const string ProviderId = "astica";
    private const string ProviderName = "Astica";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public AsticaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://voice.astica.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private string ResolveRequiredApiKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        return key;
    }

    private void ApplyAuthHeader(string apiKey)
    {
        _client.DefaultRequestHeaders.Remove("X-API-Key");
        _client.DefaultRequestHeaders.Add("X-API-Key", apiKey);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await ListModelsInternal(cancellationToken);

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => this.SpeechSamplingAsync(chatRequest, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => this.SpeechResponseAsync(options, cancellationToken);

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => this.ImageRequestInternal(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

