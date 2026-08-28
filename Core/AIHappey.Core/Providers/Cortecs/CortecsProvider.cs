using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Core.AI;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public CortecsProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.cortecs.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Cortecs)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Cortecs).ToLowerInvariant();

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (await this.IsTranscriptionModelAsync(options.Model, cancellationToken))
        {
            var unifiedResponse = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return unifiedResponse.ToChatCompletion();
        }

        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);

        return EnrichChatCompletionWithGatewayCost(response);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await this.IsTranscriptionModelAsync(options.Model, cancellationToken))
        {
            await foreach (var streamEvent in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return streamEvent.ToChatCompletionUpdate();
            }

            yield break;
        }

        ApplyAuthHeader();

        string? lastFinishReason = null;
        await foreach (var update in this.GetChatCompletions(_client,
                     options, cancellationToken: cancellationToken))
        {
            CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
            yield return EnrichChatCompletionUpdateWithGatewayCost(update);
        }
    }


    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => this.TranscriptionRequestInternal(imageRequest, cancellationToken);


    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();


    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (await this.IsTranscriptionModelAsync(options.Model, cancellationToken))
        {
            var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return result.ToResponseResult();
        }

        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options, cancellationToken: cancellationToken);

        return response;
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {

        if (await this.IsTranscriptionModelAsync(options.Model, cancellationToken))
        {
            await foreach (var responsePart in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .ToResponseStreamParts(cancellationToken))
                yield return responsePart;

            yield break;
        }


        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(_client,
           options,
           cancellationToken: cancellationToken))
        {

            yield return update;
        }
    }


    public async Task<MessagesResponse> MessagesAsync(
     MessagesRequest request,
     Dictionary<string, string> headers,
     CancellationToken cancellationToken = default)
    {
        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return result.ToMessagesResponse();
        }

        ApplyAuthHeader();

        return await this.GetMessage(_client,
            request,
            headers: headers,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
        {
            await foreach (var streamEvent in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                foreach (var part in streamEvent.ToMessageStreamParts())
                    yield return part;
            }

            yield break;
        }

        ApplyAuthHeader();

        await foreach (var part in this.GetMessages(_client, request, headers: headers, cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            yield return part;
        }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var stream = await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.OpenAICompatibleVercelTranscriptionStreamingAsync(
            request,
            GetIdentifier(),
            cancellationToken: cancellationToken);
    }
}
