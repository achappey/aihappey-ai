using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.InferexAI;

public sealed partial class InferexAIProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper asyncCacheHelper) : IModelProvider
{
    private const string ProviderId = "inferexai";
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient(ProviderId);
        client.BaseAddress = new Uri("https://api.inferexai.in/");
        return client;
    }

    public string GetIdentifier() => ProviderId;

    private string ResolveKey()
        => keyResolver.Resolve(ProviderId) is { Length: > 0 } key && !string.IsNullOrWhiteSpace(key)
            ? key : throw new InvalidOperationException("No InferexAI API key.");

    private void ApplyAuthHeader()
        => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ResolveKey());

    private static string NativeModel(string? model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        return model.StartsWith(ProviderId + "/", StringComparison.OrdinalIgnoreCase)
            ? model[(ProviderId.Length + 1)..] : model;
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        options.Model = NativeModel(options.Model);
        ApplyAuthHeader();
        var result = await this.GetChatCompletion(_client, options, cancellationToken: cancellationToken);
        if (result.AdditionalProperties?.TryGetValue("error", out var error) == true)
            throw new InvalidOperationException($"InferexAI chat failed: {error}");
        return result;
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        options.Model = NativeModel(options.Model);
        ApplyAuthHeader();
        await foreach (var update in this.GetChatCompletions(_client, options, cancellationToken: cancellationToken))
        {
            if (update.AdditionalProperties?.TryGetValue("error", out var error) == true)
                throw new InvalidOperationException($"InferexAI chat stream failed: {error}");
            yield return update;
        }
    }

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        var model = await ResolveModel(request.Model, cancellationToken);
        return model.Type switch
        {
            "speech" => await this.ExecuteUnifiedSpeechAsync(request, cancellationToken),
            "transcription" => await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken, UnifiedTranscriptionOptions(request)),
            "language" => await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken),
            _ => throw new NotSupportedException($"InferexAI model type '{model.Type}' is not a conversation model.")
        };
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await ResolveModel(request.Model, cancellationToken);
        var stream = model.Type switch
        {
            "speech" => this.StreamUnifiedSpeechAsync(request, cancellationToken, defaultResponseFormat: "pcm", useProviderScopedOptions: true),
            "transcription" => this.StreamUnifiedTranscriptionAsync(request, cancellationToken, UnifiedTranscriptionOptions(request)),
            "language" => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken),
            _ => throw new NotSupportedException($"InferexAI model type '{model.Type}' is not a conversation model.")
        };
        await foreach (var part in stream.WithCancellation(cancellationToken))
            yield return part;
    }

    internal static Dictionary<string, JsonElement>? UnifiedTranscriptionOptions(AIRequest request)
    {
        if (request.Metadata?.TryGetValue(ProviderId, out var options) != true || options is null)
            return null;
        var root = JsonSerializer.SerializeToElement(options);
        if (root.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("InferexAI unified provider options must be an object.");
        return root.EnumerateObject().Where(p => p.Name is not ("headers" or "tools" or "providerMetadata"))
            .ToDictionary(p => p.Name, p => p.Value.Clone());
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(ProviderId), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(ProviderId), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(ProviderId))
                yield return uiPart;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default) => throw new NotSupportedException("InferexAI documents a bearer-authenticated STT WebSocket, not ephemeral realtime tokens.");
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("The InferexAI /v1/understand schema could not be verified; it is not assumed to match the decisions contract.");
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("The InferexAI intent schema could not be verified.");
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
