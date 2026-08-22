using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.VLMRun;

public partial class VLMRunProvider : IModelProvider, ISkillProvider
{
    private const string VLMRunApiBaseUrl = "https://api.vlm.run/";
    private const string VLMRunApiChatCompletionsEndpoint = VLMRunApiBaseUrl + "v1/chat/completions";
    private const string VLMRunGatewayBaseUrl = "https://gateway.vlm.run/v1/openai/";
    private const string VLMRunGatewayModelsEndpoint = VLMRunGatewayBaseUrl + "models";
    private const string VLMRunGatewayChatCompletionsEndpoint = VLMRunGatewayBaseUrl + "chat/completions";
    private const string VLMRunGatewayTranscriptionsEndpoint = VLMRunGatewayBaseUrl + "audio/transcriptions";

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public VLMRunProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri(VLMRunApiBaseUrl);
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(VLMRun)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = NormalizeVLMRunModel(options.Model);
        var routedOptions = CloneVLMRunChatOptions(options, model);

        return await this.GetChatCompletion(_client,
             routedOptions,
             relativeUrl: ResolveVLMRunChatCompletionsEndpoint(model),
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var model = NormalizeVLMRunModel(options.Model);
        var routedOptions = CloneVLMRunChatOptions(options, model);

        return this.GetChatCompletions(_client,
                    routedOptions,
                    relativeUrl: ResolveVLMRunChatCompletionsEndpoint(model),
                    cancellationToken: cancellationToken);
    }

    private static string NormalizeVLMRunModel(string? model)
    {
        var normalized = model?.Trim() ?? string.Empty;
        const string providerPrefix = "vlmrun/";

        if (normalized.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[providerPrefix.Length..];

        if (string.IsNullOrWhiteSpace(normalized))
            throw new ArgumentException("A VLM Run model id is required.", nameof(model));

        return normalized;
    }

    private static bool IsVLMRunOrionModel(string model)
        => model.StartsWith("vlmrun-orion-", StringComparison.OrdinalIgnoreCase);

    private static string ResolveVLMRunChatCompletionsEndpoint(string model)
        => IsVLMRunOrionModel(model)
            ? VLMRunApiChatCompletionsEndpoint
            : VLMRunGatewayChatCompletionsEndpoint;

    private static ChatCompletionOptions CloneVLMRunChatOptions(ChatCompletionOptions source, string model)
        => new()
        {
            Model = model,
            Temperature = source.Temperature,
            ParallelToolCalls = source.ParallelToolCalls,
            Stream = source.Stream,
            ReasoningEffort = source.ReasoningEffort,
            Messages = source.Messages,
            Tools = source.Tools,
            ToolChoice = source.ToolChoice,
            ResponseFormat = source.ResponseFormat,
            Store = source.Store,
            StreamOptions = source.StreamOptions,
            Metadata = source.Metadata,
            Headers = source.Headers,
            AdditionalProperties = source.AdditionalProperties
        };

    public string GetIdentifier() => nameof(VLMRun).ToLowerInvariant();

   
    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
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
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
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

        yield break;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        if (IsVLMRunAgentModel(request.Model))
            return await ExecuteAgentUnifiedAsync(request, cancellationToken);

        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);

        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var stream = IsVLMRunAgentModel(request.Model)
            ? StreamAgentUnifiedAsync(request, cancellationToken)
            : await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
                ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
                : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

        await foreach (var streamEvent in stream.WithCancellation(cancellationToken))
            yield return streamEvent;
    }

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
        throw new NotImplementedException();
    }
}
