using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.ChatCompletions.Models;
using Microsoft.AspNetCore.Http;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IHttpContextAccessor _context;
    private readonly HttpClient _client;

    public PollinationsProvider(IApiKeyResolver keyResolver,
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _context = httpContextAccessor;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://text.pollinations.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }



    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public string GetIdentifier() => nameof(Pollinations).ToLowerInvariant();

    private readonly ModelPricing Free = new()
    {
        Input = 0,
        Output = 0
    };

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var streamEvent in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return streamEvent.ToChatCompletionUpdate();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(
            request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => await this.IsImageModelAsync(request.Model, cancellationToken)
            ? await this.ExecuteUnifiedImageAsync(request, cancellationToken)
            : throw new NotSupportedException($"Pollinations model '{request.Model}' is not an image model.");

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!await this.IsImageModelAsync(request.Model, cancellationToken))
            throw new NotSupportedException($"Pollinations model '{request.Model}' is not an image model.");

        await foreach (var streamEvent in this.StreamUnifiedImageAsync(request, cancellationToken)
                           .WithCancellation(cancellationToken))
            yield return streamEvent;
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

    

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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
