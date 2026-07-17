using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.NinjaChat;

public partial class NinjaChatProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly IHttpClientFactory _httpClientFactory;

    private readonly AsyncCacheHelper _memoryCache;

    public NinjaChatProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _httpClientFactory = httpClientFactory;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://www.ninjachat.ai/api/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(NinjaChat)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(NinjaChat).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(request.Model))
        {
            var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
            return result.ToMessagesResponse();
        }

        return await this.GetMessage(
            _client,
            request,
            headers: headers,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeSearchModel(request.Model))
        {
            await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            {
                foreach (var item in part.ToMessageStreamParts())
                    yield return item;
            }

            yield break;
        }

        await foreach (var part in this.GetMessages(_client,
            request,
            headers: headers,
            cancellationToken: cancellationToken))
        {
            yield return part;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return IsNativeSearchModel(request.Model)
            ? ExecuteUnifiedSearchAsync(request, cancellationToken)
            : this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return IsNativeSearchModel(request.Model)
            ? StreamUnifiedSearchAsync(request, cancellationToken)
            : IsEnsembleModel(request.Model)
            ? StreamUnifiedEnsembleAsync(request, cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
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
