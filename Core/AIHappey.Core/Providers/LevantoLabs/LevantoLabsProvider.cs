using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.LevantoLabs;

public sealed partial class LevantoLabsProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://sage.levanto.ai/");
        return client;
    }

    public string GetIdentifier() => "levantolabs";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No LevantoLabs API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IEnumerable<Model>>(GetIdentifier().GetModels());
    }

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteOpenAIDecisionRequestAsync(request, cancellationToken);

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => ExecuteDecisionRequestAsync(request, cancellationToken);

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs documents Sage Decisions, not chat completions.");

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs documents Sage Decisions, not chat completions.");

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs documents Sage Decisions, not Responses.");

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs documents Sage Decisions, not Responses.");

    public IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs documents Sage Decisions, not chat completions.");

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document image generation.");

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document transcription.");

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document speech generation.");

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document reranking.");

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document realtime.");

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document Messages.");

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document Messages.");

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document speech generation.");

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document speech generation.");

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document image generation.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(
        OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document image generation.");

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document image editing.");

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(
        OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document image editing.");

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document transcription.");

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document transcription.");

    public Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document video generation.");

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document video generation.");

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document embeddings.");

    public Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document embeddings.");

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document transcription.");

    public Task<LiveSessionResponse> CreateLiveSessionAsync(
        LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document realtime.");

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document realtime.");

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document realtime.");

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("LevantoLabs does not document speech generation.");
}
