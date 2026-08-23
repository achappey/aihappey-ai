using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Novita;

public partial class NovitaProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;
    private readonly IHttpClientFactory _factory;

    private readonly AsyncCacheHelper _memoryCache;

    public NovitaProvider(IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.novita.ai/");
        _factory = httpClientFactory;
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Novita)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Novita).ToLowerInvariant();



    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        if (model.Type == "speech")
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
                           unifiedRequest,
                           cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
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

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request,          
            cancellationToken: cancellationToken);

        return result.Response;
    }

    public async Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var openAIRequest = request.ToOpenAIEmbeddingRequest(GetIdentifier());
        var result = await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            openAIRequest,            
            cancellationToken: cancellationToken);

        return result.ToEmbeddingResponse(GetIdentifier().CreatePrimitiveProviderMetadata());
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
