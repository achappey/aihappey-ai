using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses.Extensions;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.MindsHub;

public sealed partial class MindsHubProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.mindshub.ai/");
        return client;
    }

    public string GetIdentifier() => "mindshub";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No MindsHub API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => ListMindsHubModelsAsync(cancellationToken);

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await _client.GetChatCompletion(
            options,
            GetIdentifier(),
            headers: GetChatHeaders(options),
            ct: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var update in _client.GetChatCompletionUpdates(
            options,
            GetIdentifier(),
            headers: GetChatHeaders(options),
            ct: cancellationToken))
            yield return update;
    }

    private IReadOnlyDictionary<string, string> GetChatHeaders(ChatCompletionOptions options)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in this.SetDefaultChatCompletionProperties(options) ?? [])
            headers[name] = value;
        foreach (var (name, value) in options.Headers ?? [])
            headers[name] = value;

        return FilterHeaders(headers);
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var request = CopyResponseRequest(options);
        request.Stream = false;
        var headers = MergeResponseHeaders(
            this.SetDefaultResponseProperties(request),
            request.Headers);
        return await _client.GetResponses(
            request,
            GetIdentifier(),
            headers: headers,
            ct: cancellationToken);
    }

    public async IAsyncEnumerable<AIHappey.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var request = CopyResponseRequest(options);
        request.Stream = true;
        var headers = MergeResponseHeaders(
            this.SetDefaultResponseProperties(request),
            request.Headers);
        await foreach (var part in _client.GetResponsesUpdates(
            request,
            GetIdentifier(),
            headers: headers,
            ct: cancellationToken))
            yield return part;
    }

    private static ResponseRequest CopyResponseRequest(ResponseRequest source)
        => new()
        {
            Model = source.Model,
            Instructions = source.Instructions,
            Input = source.Input,
            Temperature = source.Temperature,
            TopP = source.TopP,
            Truncation = source.Truncation,
            MaxOutputTokens = source.MaxOutputTokens,
            TopLogprobs = source.TopLogprobs,
            ParallelToolCalls = source.ParallelToolCalls,
            Stream = source.Stream,
            Store = source.Store,
            ServiceTier = source.ServiceTier,
            Text = source.Text,
            Include = source.Include is null ? null : [.. source.Include],
            Metadata = source.Metadata is null ? null : new(source.Metadata),
            Headers = FilterHeaders(source.Headers),
            Tools = source.Tools is null ? null : [.. source.Tools],
            ToolChoice = source.ToolChoice,
            Reasoning = source.Reasoning,
            ContextManagement = source.ContextManagement?.Select(item => item.Clone()).ToArray(),
            AdditionalProperties = source.AdditionalProperties is null
                ? null
                : source.AdditionalProperties.ToDictionary(item => item.Key, item => item.Value.Clone()),
            Background = source.Background,
            PromptCacheKey = source.PromptCacheKey,
            PromptCacheOptions = source.PromptCacheOptions
        };

    private static Dictionary<string, string> MergeResponseHeaders(
        IReadOnlyDictionary<string, string>? defaults,
        IReadOnlyDictionary<string, string>? request)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { defaults, request })
        {
            if (source is null)
                continue;
            foreach (var (name, value) in source)
                if (!IsCredentialHeader(name))
                    headers[name] = value;
        }
        return headers;
    }

    private static Dictionary<string, string> FilterHeaders(
        IReadOnlyDictionary<string, string>? source)
        => source is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : source.Where(item => !IsCredentialHeader(item.Key))
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase);

    private static bool IsCredentialHeader(string name)
        => name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Api-Key", StringComparison.OrdinalIgnoreCase)
            || name.Equals("X-Goog-Api-Key", StringComparison.OrdinalIgnoreCase)
            || AIHappey.Responses.ProviderCredentialHeaders.IsProviderKey(name);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var streamEvent in this.StreamUnifiedViaChatCompletionsAsync(
            unifiedRequest,
            cancellationToken: cancellationToken))
        {
            foreach (var part in streamEvent.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Stream = false;
        this.SetDefaultMessagesProperties(request);
        return _client.PostMessages(
            request,
            GetIdentifier(),
            MergeMessagesHeaders(headers),
            ct: cancellationToken);
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        request.Stream = true;
        this.SetDefaultMessagesProperties(request);
        await foreach (var part in _client.PostMessagesStreaming(
            request,
            GetIdentifier(),
            MergeMessagesHeaders(headers),
            ct: cancellationToken))
            yield return part;
    }

    private static Dictionary<string, string> MergeMessagesHeaders(Dictionary<string, string> headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["anthropic-version"] = "2023-06-01"
        };
        foreach (var (name, value) in headers)
        {
            if (IsCredentialHeader(name))
                continue;

            result[name] = value;
        }
        return result;
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return SendOpenAIEmbeddingAsync(request, cancellationToken);
    }

    private async Task<OpenAIEmbeddingResponse> SendOpenAIEmbeddingAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken)
        => (await this.OpenAICompatibleEmbeddingRequestAsync(
            _client,
            request,
            cancellationToken: cancellationToken)).Response;

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

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteDecisionRequestAsync(request, cancellationToken);

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw Unsupported();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw Unsupported();

    private NotSupportedException Unsupported()
        => new($"{GetIdentifier()} does not document this API.");
}
