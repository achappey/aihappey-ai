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
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;
using Microsoft.Extensions.Http;

namespace AIHappey.Core.Providers.SerenityEdge;

public sealed partial class SerenityEdgeProvider : IModelProvider
{
    private static readonly HashSet<string> ChatExtensionProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "max_tokens", "top_p", "n", "stop", "presence_penalty", "frequency_penalty", "seed", "user"
    };

    private readonly IApiKeyResolver _keyResolver;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;

    public SerenityEdgeProvider(IApiKeyResolver keyResolver, AsyncCacheHelper cache, IHttpClientFactory factory)
    {
        _keyResolver = keyResolver;
        _cache = cache;
        _client = factory.CreateClient();
        _client.BaseAddress = new Uri("https://api.serenityedge.ai/");
    }

    public string GetIdentifier() => "serenityedge";

    private string ResolveApiKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Serenity Edge API key.");

        return key;
    }

    private void ApplyAuthHeader()
        => _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ResolveApiKey());

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public async Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateChatOptions(options);
        ApplyAuthHeader();
        return await this.GetChatCompletion(_client, options, relativeUrl: "v1/chat/completions",
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateChatOptions(options);
        ApplyAuthHeader();
        await foreach (var update in this.GetChatCompletions(_client, options,
            relativeUrl: "v1/chat/completions", cancellationToken: cancellationToken))
            yield return update;
    }

    private static void ValidateChatOptions(ChatCompletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AdditionalProperties?.Keys.Any(name => !ChatExtensionProperties.Contains(name)) == true)
            throw new NotSupportedException("The Chat Completions request contains options not documented by Serenity Edge.");
        if (options.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge temperature must be between 0 and 2.");
        if (options.AdditionalProperties?.TryGetValue("max_tokens", out var maxTokens) == true
            && (!maxTokens.TryGetInt32(out var maxTokensValue) || maxTokensValue is < 1 or > 32768))
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge max_tokens must be between 1 and 32768.");
        if (options.AdditionalProperties?.TryGetValue("top_p", out var topP) == true
            && (!topP.TryGetDouble(out var topPValue) || !double.IsFinite(topPValue) || topPValue is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge top_p must be between 0 and 1.");
        if (options.AdditionalProperties?.TryGetValue("n", out var count) == true
            && (!count.TryGetInt32(out var countValue) || countValue < 1))
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge n must be at least 1.");
        foreach (var name in new[] { "presence_penalty", "frequency_penalty" })
        {
            if (options.AdditionalProperties?.TryGetValue(name, out var penalty) == true
                && (!penalty.TryGetDouble(out var value) || !double.IsFinite(value) || value is < -2 or > 2))
                throw new ArgumentOutOfRangeException(nameof(options), $"Serenity Edge {name} must be between -2 and 2.");
        }
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResponseOptions(options);
        ApplyAuthHeader();
        return await this.GetResponse(_client, options, relativeUrl: "v1/responses",
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateResponseOptions(options);
        ApplyAuthHeader();
        await foreach (var part in this.GetResponses(_client, options, relativeUrl: "v1/responses",
            cancellationToken: cancellationToken))
            yield return part;
    }

    private static void ValidateResponseOptions(ResponseRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Store == true || options.Background == true
            || options.PromptCacheKey is not null
            || options.PromptCacheOptions is not null
            || options.ServiceTier is not null
            || options.TopLogprobs is not null
            || options.ContextManagement is not null)
            throw new NotSupportedException("Serenity Edge Responses are stateless and do not support storage, continuation, background jobs, prompt templates, service tiers, context management, or log probabilities.");

        if (options.AdditionalProperties?.Keys.Any(name =>
                name is "previous_response_id" or "conversation" or "prompt") == true)
            throw new NotSupportedException("Serenity Edge Responses do not support continuation or prompt templates.");

        if (options.Truncation is not null && options.Truncation != TruncationStrategy.Disabled)
            throw new NotSupportedException("Serenity Edge Responses only supports truncation: disabled.");

        if (options.Include?.Contains("message.output_text.logprobs", StringComparer.Ordinal) == true)
            throw new NotSupportedException("Serenity Edge Responses does not support message.output_text.logprobs.");
        if (options.Include?.Any(include => include is not ("reasoning.encrypted_content" or "message.input_image.image_url")) == true)
            throw new NotSupportedException("Serenity Edge Responses only accepts reasoning.encrypted_content and message.input_image.image_url in include.");

        if (options.MaxOutputTokens is < 1)
            throw new ArgumentOutOfRangeException(nameof(options), "max_output_tokens must be at least 1.");
        if (options.Temperature is < 0 or > 2)
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge temperature must be between 0 and 2.");
        if (options.TopP is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(options), "Serenity Edge top_p must be between 0 and 1.");
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => ListSerenityEdgeModelsAsync(cancellationToken);

    public Task<MessagesResponse> MessagesAsync(
        MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Serenity Edge does not document a Messages API.");

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Serenity Edge does not document a Messages API.");

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
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
    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RealtimeTokenResponse> CreateRealtimeTokenAsync(string model, RealtimeTokenRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<RealtimeSessionResponse> CreateRealtimeSessionAsync(string model, RealtimeSessionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
