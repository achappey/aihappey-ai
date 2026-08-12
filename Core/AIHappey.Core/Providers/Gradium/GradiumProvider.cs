using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Extensions;

namespace AIHappey.Core.Providers.Gradium;

public partial class GradiumProvider : IModelProvider, IUnifiedModelProvider
{
    private const string ProviderId = "gradium";
    private const string ProviderName = "Gradium";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public GradiumProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://eu.api.gradium.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Add("x-api-key", key);
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).WithCancellation(cancellationToken))
        {
            yield return streamEvent.ToChatCompletionUpdate();
        }
    }



    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).WithCancellation(cancellationToken))
        {
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).WithCancellation(cancellationToken))
        {
            yield return streamEvent.ToResponseStreamPart();
        }
    }


    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();



    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken).WithCancellation(cancellationToken))
        {
            foreach (var part in streamEvent.ToMessageStreamParts())
                yield return part;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedTranscriptionAsync(request, cancellationToken);

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
}
