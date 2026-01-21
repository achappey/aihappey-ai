using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.Lingvanex;

/// <summary>
/// Lingvanex translator service provider.
/// Exposes one translation model per target language: <c>translate/&lt;full_code&gt;</c>
/// where <c>full_code</c> is like <c>en_GB</c>, <c>de_DE</c>.
/// </summary>
public sealed partial class LingvanexProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public LingvanexProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api-b2b.backenster.com/b1/api/v3/");
    }

    public string GetIdentifier() => "lingvanex";

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Lingvanex API key.");

        _client.DefaultRequestHeaders.Remove("Authorization");
        _client.DefaultRequestHeaders.Add("Authorization", key);
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
        ApplyAuthHeader();
        return await ListTranslationModelsAsync(cancellationToken);
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentNullException.ThrowIfNullOrEmpty(modelId);

        var model = await this.GetModel(modelId, cancellationToken: cancellationToken);
        if (!string.Equals(model.Type, "language", StringComparison.OrdinalIgnoreCase))
            throw new NotImplementedException();

        return await TranslateSamplingAsync(chatRequest, modelId, cancellationToken);
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken);
        if (!string.Equals(model.Type, "language", StringComparison.OrdinalIgnoreCase))
            throw new NotImplementedException();

        await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var modelId = options.Model ?? throw new ArgumentException(nameof(options.Model));
        var model = await this.GetModel(modelId, cancellationToken);

        if (!string.Equals(model.Type, "language", StringComparison.OrdinalIgnoreCase))
            throw new NotImplementedException();

        // Lingvanex metadata is NOT supported on Responses endpoint initially (model-only).
        return await TranslateResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

