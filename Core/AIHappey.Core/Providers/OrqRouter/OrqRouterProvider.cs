using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Messages.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.OrqRouter;

public partial class OrqRouterProvider : IModelProvider
{
    private const string ProviderId = "orqrouter";
    private const string ProviderName = "OrqRouter";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public OrqRouterProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.orq.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(OrqRouter)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client, options,
            relativeUrl: "v2/router/chat/completions",
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client, options,
            relativeUrl: "v2/router/chat/completions",
            cancellationToken: cancellationToken);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
            {
                yield return uiPart;
            }
        }
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var response = await this.GetResponse(_client,
                   options,
                   relativeUrl: "v3/router/responses",
                   cancellationToken: cancellationToken);

        return response;
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        await foreach (var update in this.GetResponses(_client,
           options,
           relativeUrl: "v3/router/responses",
           cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => OrqRouterImageRequest(request, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => OrqRouterTranscriptionRequest(request, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => OrqRouterSpeechRequest(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => OrqRouterRerankingRequest(request, cancellationToken);

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

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
    }


    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

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
}
