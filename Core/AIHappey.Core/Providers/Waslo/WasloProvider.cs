using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Waslo;

public sealed partial class WasloProvider : IModelProvider, IUnifiedModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IEndUserIdResolver _endUserIdResolver;
    private readonly HttpClient _client;

    public WasloProvider(IApiKeyResolver keyResolver, IEndUserIdResolver endUserIdResolver,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _endUserIdResolver = endUserIdResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.waslo.io/");
    }

    public string GetIdentifier() => "waslo";

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier()))
            ? [] : [new Model
            {
                Id = "reply".ToModelId(GetIdentifier()), Name = "Waslo Agent Reply",
                Description = "Stateful Waslo agent; requires a stable userId. Supports one image, audio, video or PDF and optional voice output.",
                OwnedBy = "Waslo", Type = "language", Tags = ["agent"]
            }]);

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return part.ToChatCompletionUpdate();
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = chatRequest.ToUnifiedRequest(GetIdentifier());
        // This resolver is defined for the Vercel chat request only. Other protocols supply
        // waslo.userId explicitly; never use a per-conversation/request ID for Waslo memory.
        var userId = _endUserIdResolver.Resolve(chatRequest);
        if (!string.IsNullOrWhiteSpace(userId))
            request = WithResolvedUserId(request, userId);
        await foreach (var part in StreamUnifiedAsync(request, cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    private static AIRequest WithResolvedUserId(AIRequest request, string userId)
    {
        var metadata = new Dictionary<string, object?>(request.Metadata ?? []);
        var scoped = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal);
        if (metadata.TryGetValue("waslo", out var value) && value is not null)
        {
            var json = value is System.Text.Json.JsonElement element ? element
                : System.Text.Json.JsonSerializer.SerializeToElement(value, System.Text.Json.JsonSerializerOptions.Web);
            if (json.ValueKind != System.Text.Json.JsonValueKind.Object)
                throw new ArgumentException("Waslo provider metadata must be an object.", nameof(request));
            foreach (var property in json.EnumerateObject()) scoped[property.Name] = property.Value.Clone();
        }
        scoped["userId"] = System.Text.Json.JsonSerializer.SerializeToElement(userId);
        metadata["waslo"] = System.Text.Json.JsonSerializer.SerializeToElement(scoped);
        return new AIRequest
        {
            ProviderId = request.ProviderId, Model = request.Model, Id = request.Id,
            Instructions = request.Instructions, Input = request.Input, Temperature = request.Temperature,
            TopP = request.TopP, MaxOutputTokens = request.MaxOutputTokens, MaxToolCalls = request.MaxToolCalls,
            Stream = request.Stream, ParallelToolCalls = request.ParallelToolCalls, ToolChoice = request.ToolChoice,
            ResponseFormat = request.ResponseFormat, Tools = request.Tools, Headers = request.Headers,
            Verbosity = request.Verbosity, Metadata = metadata
        };
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
