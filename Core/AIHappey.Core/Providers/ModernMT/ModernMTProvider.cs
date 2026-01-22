using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.ModernMT;

/// <summary>
/// ModernMT translator service provider.
/// Exposes one translation model per target language: <c>translate/&lt;languageCode&gt;</c>
/// where <c>languageCode</c> is like <c>en</c>, <c>de</c>, <c>zh-CN</c>.
/// </summary>
public sealed partial class ModernMTProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public ModernMTProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.modernmt.com/");
    }

    public string GetIdentifier() => "modernmt";

    private string GetKeyOrThrow()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No ModernMT API key.");
        return key.Trim();
    }

    private void ApplyAuthHeader()
    {
        var key = GetKeyOrThrow();

        _client.DefaultRequestHeaders.Remove("MMT-ApiKey");
        _client.DefaultRequestHeaders.Add("MMT-ApiKey", key);
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotImplementedException();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Avoid throwing during model discovery when the key is not configured.
        if (string.IsNullOrWhiteSpace(_keyResolver.Resolve(GetIdentifier())))
            return [];

        ApplyAuthHeader();
        return await ListTranslationModelsAsync(cancellationToken);
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        // Ensure key is present on request-time.
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentNullException.ThrowIfNullOrEmpty(modelId);

        return await TranslateSamplingAsync(chatRequest, modelId, cancellationToken);
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Ensure key is present on request-time.
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        // Ensure key is present on request-time.
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        return await TranslateResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

