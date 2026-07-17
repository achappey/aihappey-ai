using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public MiniMaxProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.minimax.io/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(MiniMax)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);

        return this.EnrichChatCompletionWithCatalogGatewayCost(response, options.Model);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        string? lastFinishReason = null;
        await foreach (var update in this.GetChatCompletions(_client,
                            options,
                            cancellationToken: cancellationToken))
        {
            CatalogPricingCostingExtensions.NormalizeStreamingUpdateForGatewayCost(update, ref lastFinishReason);
            yield return this.EnrichChatCompletionUpdateWithCatalogGatewayCost(update, options.Model);
        }
    }

    public string GetIdentifier() => nameof(MiniMax).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken: cancellationToken);

        if (model.Type == "speech")
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options, cancellationToken: cancellationToken);

        return this.EnrichResponseWithCatalogGatewayCost(response, options.Model);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(_client,
           options,
           cancellationToken: cancellationToken))
        {
            if (update is Responses.Streaming.ResponseCompleted completed)
                this.EnrichResponseWithCatalogGatewayCost(completed.Response, options.Model);

            yield return update;
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(
       MessagesRequest request,
       Dictionary<string, string> headers,
       CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetMessage(_client,
            request,
            "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken);

        return this.EnrichMessagesResponseWithCatalogGatewayCost(response, request.Model);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var part in this.GetMessages(_client,
            request,
            "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken))
        {
            yield return this.EnrichMessageStreamPartWithCatalogGatewayCost(part, request.Model);
        }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var response = await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        return this.EnrichUnifiedResponseWithCatalogGatewayCost(response, request.Model);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(
                           request,
                           cancellationToken: cancellationToken))
        {
            yield return this.EnrichUnifiedStreamEventWithCatalogGatewayCost(streamEvent, request.Model);
        }
    }

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
