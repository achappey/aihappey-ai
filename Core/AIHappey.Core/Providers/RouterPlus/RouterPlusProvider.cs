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
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.RouterPlus;

public sealed partial class RouterPlusProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper memoryCache) : IModelProvider, IUnifiedModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.routerplus.com/");
        return client;
    }

    public string GetIdentifier() => "routerplus";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No RouterPlus API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return await this.GetChatCompletion(_client, options,
            relativeUrl: "v1/chat/completions", cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        await foreach (var update in this.GetChatCompletions(_client, options,
            relativeUrl: "v1/chat/completions", cancellationToken: cancellationToken))
            yield return update;
    }

    public Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetResponse(_client, options,
            relativeUrl: "v1/responses", cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetResponses(_client, options,
            relativeUrl: "v1/responses", cancellationToken: cancellationToken);
    }

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetMessage(_client, request,
            relativeUrl: "v1/messages", headers: headers, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetMessages(_client, request,
            relativeUrl: "v1/messages", headers: headers, cancellationToken: cancellationToken);
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await this.IsVideoModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedVideoAsync(request, cancellationToken: cancellationToken);
        if (await this.IsImageModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedImageAsync(request, cancellationToken);
        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stream = await this.IsVideoModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedVideoAsync(request, cancellationToken: cancellationToken)
            : await this.IsImageModelAsync(request.Model, cancellationToken)
                ? this.StreamUnifiedImageAsync(request, cancellationToken)
                : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

        await foreach (var item in stream.WithCancellation(cancellationToken))
            yield return item;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(
        SpeechRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<LiveSessionResponse> CreateLiveSessionAsync(
        LiveSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(
        string model,
        RealtimeTokenRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(
        string model,
        RealtimeSessionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(
        StreamingSpeechRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
