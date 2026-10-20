using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.AspNetCore.Http;

namespace AIHappey.Core.Providers.Yandex;

public sealed partial class YandexProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper asyncCacheHelper,
    IHttpContextAccessor httpContextAccessor) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://llm.api.cloud.yandex.net/");
        return client;
    }

    public string GetIdentifier() => "yandex";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Yandex AI Studio API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Api-Key", key);
        _client.DefaultRequestHeaders.Remove("OpenAI-Project");
        var folder = httpContextAccessor.HttpContext?.Request.Headers["OpenAI-Project"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(folder))
            _client.DefaultRequestHeaders.Add("OpenAI-Project", folder);
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => ListYandexModelsAsync(cancellationToken);

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await this.GetChatCompletion(
            _client, options, relativeUrl: "v1/chat/completions", cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletions(
            _client, options, relativeUrl: "v1/chat/completions", cancellationToken: cancellationToken);
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(
            request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
                           options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
