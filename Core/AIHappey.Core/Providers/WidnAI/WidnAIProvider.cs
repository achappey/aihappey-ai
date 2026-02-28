using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.WidnAI;

public partial class WidnAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public WidnAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.widn.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(WidnAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = null;
        _client.DefaultRequestHeaders.Remove("X-API-KEY");
        _client.DefaultRequestHeaders.Add("X-API-KEY", key.Trim());
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsTranslationModelId(options.Model))
        {
            ApplyAuthHeader();
            return await TranslateChatCompletionAsync(options, cancellationToken);
        }

        ApplyAuthHeader();

        return await _client.GetChatCompletion(
              options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        if (IsTranslationModelId(options.Model))
        {
            ApplyAuthHeader();
            return TranslateChatCompletionUpdatesAsync(options, cancellationToken);
        }

        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options, ct: cancellationToken);
    }

    public string GetIdentifier() => nameof(WidnAI).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var modelId = chatRequest.GetModel();
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        if (IsTranslationModelId(modelId))
        {
            ApplyAuthHeader();
            return await TranslateSamplingAsync(chatRequest, cancellationToken);
        }

        return await this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        if (!IsTranslationModelId(modelId))
            throw new NotImplementedException();

        ApplyAuthHeader();
        
        return await TranslateResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private static bool IsTranslationModelId(string modelId)
        => TryParseTranslationModel(modelId, out _, out _, out _);
}
