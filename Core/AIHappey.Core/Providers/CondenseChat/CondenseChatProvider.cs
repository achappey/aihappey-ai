using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.CondenseChat;

public partial class CondenseChatProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;
    private readonly IServiceProvider _serviceProvider;

    public CondenseChatProvider(IApiKeyResolver keyResolver,
        IServiceProvider serviceProvider,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _serviceProvider = serviceProvider;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.condense.chat/");
    }

    private void ApplyOpenAIHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        var openaiKey = _keyResolver.Resolve("openai");

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(CondenseChat)} API key.");

        if (string.IsNullOrWhiteSpace(openaiKey))
            throw new InvalidOperationException($"No {nameof(OpenAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", openaiKey);

        _client.DefaultRequestHeaders.Remove("X-Condense-Auth-Token");
        _client.DefaultRequestHeaders.Add("X-Condense-Auth-Token", key);
    }

    private void ApplyAnthropicAuthHeader()
    {

        var key = _keyResolver.Resolve(GetIdentifier());
        var anthropicKey = _keyResolver.Resolve("anthropic");

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(CondenseChat)} API key.");

        if (string.IsNullOrWhiteSpace(anthropicKey))
            throw new InvalidOperationException($"No {nameof(Anthropic)} API key.");

        _client.DefaultRequestHeaders.Remove("X-Condense-Auth-Token");
        _client.DefaultRequestHeaders.Add("X-Condense-Auth-Token", key);


        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Add("x-api-key", anthropicKey);

    }

    private void ApplyAuthHeader(string model)
    {
        if (model.StartsWith("anthropic/"))
        {
            ApplyAnthropicAuthHeader();
        }
        else if (model.StartsWith("openai/"))
        {
            ApplyOpenAIHeader();
        }
        else
        {
            throw new InvalidOperationException($"Invalid model {model}.");
        }
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(options.Model);

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "openai/v1/chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(options.Model);

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "openai/v1/chat/completions",
                    cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(CondenseChat).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(options.Model!);

        var response = await this.GetResponse(_client,
                   options,
                   relativeUrl: "openai/v1/responses",
                   cancellationToken: cancellationToken);

        return response;
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(options.Model!);

        await foreach (var update in this.GetResponses(_client,
           options,
           relativeUrl: "openai/v1/responses",
           cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async Task<MessagesResponse> MessagesAsync(
      MessagesRequest request,
      Dictionary<string, string> headers,
      CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(request.Model!);

        request.Model = request.Model?.SplitModelId().Model;

        return await this.GetMessage(_client,
            request,
            relativeUrl: "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader(request.Model!);

        request.Model = request.Model?.SplitModelId().Model;

        return this.GetMessages(_client,
            request,
            relativeUrl: "anthropic/v1/messages",
            headers: headers,
            cancellationToken: cancellationToken);
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => request.Model?.StartsWith("openai/") == true ?
      this.ExecuteUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken)
      : this.ExecuteUnifiedViaMessagesAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.StartsWith("openai/") == true ?
        this.StreamUnifiedViaResponsesAsync(request, cancellationToken: cancellationToken)
        : this.StreamUnifiedViaMessagesAsync(request, cancellationToken: cancellationToken);

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
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

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}