using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.XVoice;

public partial class XVoiceProvider(
    IApiKeyResolver keyResolver,
    AsyncCacheHelper cache,
    IHttpClientFactory httpClientFactory) : IModelProvider, IUnifiedModelProvider
{
    private const string ProviderId = "xvoice";
    private const string ProviderName = "xVoice";
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api.xvoice.dev/");
        return client;
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(ProviderId);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No xVoice API key.");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var requestId = response.Headers.TryGetValues("x-request-id", out var ids)
                ? string.Join(", ", ids) : "unknown";
            throw new HttpRequestException(
                $"xVoice request failed ({(int)response.StatusCode}): {body}; " +
                $"request-id={requestId}; " +
                $"retry-after={response.Headers.RetryAfter}", null, response.StatusCode);
        }
    }

    private static async IAsyncEnumerable<JsonElement> ReadEventsAsync(
        HttpResponseMessage response, string doneType,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
            throw new InvalidOperationException("xVoice streaming response must be text/event-stream.");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await foreach (var frame in SseParser.Create(stream).EnumerateAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(frame.Data))
                continue;
            using var document = JsonDocument.Parse(frame.Data);
            var payload = document.RootElement;
            if (frame.EventType == "error" || payload.TryGetProperty("error", out _)
                || TranscriptionString(payload, "type") == "error")
                throw new InvalidOperationException($"xVoice stream failed: {payload.GetRawText()}");
            var type = TranscriptionString(payload, "type") ?? frame.EventType;
            if (TranscriptionString(payload, "type") is null)
            {
                var fields = payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
                fields["type"] = JsonSerializer.SerializeToElement(type);
                payload = JsonSerializer.SerializeToElement(fields);
            }
            yield return payload.Clone();
            if (type == doneType)
                yield break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"xVoice stream ended without {doneType} (stream_interrupted).");
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(request.Model, cancellationToken);
        return model.Type switch
        {
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken, defaultResponseFormat: "wav"),
            "transcription" => await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"xVoice model type '{model.Type}' is not supported.")
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(request.Model, cancellationToken);
        var events = model.Type switch
        {
            "speech" => this.StreamUnifiedSpeechAsync(request, cancellationToken, defaultResponseFormat: "wav", useProviderScopedOptions: true),
            "transcription" => this.StreamUnifiedTranscriptionAsync(request, cancellationToken),
            _ => throw new NotSupportedException($"xVoice model type '{model.Type}' is not supported.")
        };
        await foreach (var item in events.WithCancellation(cancellationToken))
            yield return item;
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken))
            yield return item.ToChatCompletionUpdate();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken).ToResponseStreamParts(cancellationToken))
            yield return item;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken).ToMessageStreamParts(request.Model, cancellationToken))
            yield return item;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken))
            foreach (var part in item.Event.ToUIMessagePart(ProviderId))
                yield return part;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken cancellationToken = default) => throw RealtimeNotSupported();
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw RealtimeNotSupported();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw RealtimeNotSupported();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw RealtimeNotSupported();
    private static NotSupportedException RealtimeNotSupported() => new("xVoice realtime requires a server-side bearer-authenticated WebSocket; client token/session issuance is not documented. Use file transcription or HTTP SSE.");
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
