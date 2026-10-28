using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Respan;

public sealed partial class RespanProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory) : IModelProvider, IUnifiedModelProvider
{
    private const string AnthropicMessagesPath = "api/anthropic/v1/messages";
    private const string ChatCompletionsPath = "api/chat/completions";
    private const string ResponsesPath = "api/responses";
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.respan.ai/");
        return client;
    }

    public string GetIdentifier() => "respan";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Respan API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletion(
            _client,
            options,
            relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletions(
            _client,
            options,
            relativeUrl: ChatCompletionsPath,
            cancellationToken: cancellationToken);
    }

    public Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetResponse(
            _client,
            options,
            relativeUrl: ResponsesPath,
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetResponses(
            _client,
            options,
            relativeUrl: ResponsesPath,
            cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        this.SetDefaultMessagesProperties(request);
        return _client.PostMessages(
            request,
            GetIdentifier(),
            GetMessagesHeaders(headers),
            relativeUrl: AnthropicMessagesPath,
            ct: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        this.SetDefaultMessagesProperties(request);
        return _client.PostMessagesStreaming(
            request,
            GetIdentifier(),
            GetMessagesHeaders(headers),
            relativeUrl: AnthropicMessagesPath,
            ct: cancellationToken);
    }

    private static Dictionary<string, string> GetMessagesHeaders(Dictionary<string, string>? headers)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers ?? [])
        {
            if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase)
                || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)
                || name.Equals("api-key", StringComparison.OrdinalIgnoreCase))
                continue;

            result[name] = value;
        }

        result["anthropic-version"] = "2023-06-01";
        return result;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await foreach (var item in this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return item;
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEnumerable<Model>>(GetIdentifier().GetModels());
    }

    public async Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => (await ExecuteOpenAIDecisionRequestAsync(request, null, null, cancellationToken)).Response;

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteDecisionRequestAsync(request, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var item in this.StreamUnifiedAsync(unifiedRequest, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var part in item.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
        }
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose image generation through its gateway.");

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose transcription through its gateway.");

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose speech generation through its gateway.");

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose reranking through its gateway.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose realtime through its gateway.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose speech generation through its gateway.");

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose speech generation through its gateway.");

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose image generation through its gateway.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose image generation through its gateway.");

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose image editing through its gateway.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose image editing through its gateway.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose transcription through its gateway.");

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose transcription through its gateway.");

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose video generation through its gateway.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose video generation through its gateway.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose embeddings through its gateway.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose embeddings through its gateway.");

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose transcription through its gateway.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose realtime through its gateway.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose realtime through its gateway.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose realtime through its gateway.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Respan does not expose speech generation through its gateway.");
}
