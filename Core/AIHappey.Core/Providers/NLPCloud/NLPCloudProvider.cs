using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.NLPCloud;

public partial class NLPCloudProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;
    private readonly IHttpClientFactory _factory;

    public NLPCloudProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.nlpcloud.io/v1/");
        _factory = httpClientFactory;
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(NLPCloud)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Token", key);
    }


    public string GetIdentifier() => nameof(NLPCloud).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        ArgumentNullException.ThrowIfNullOrEmpty(modelId);

        var kind = GetModelKind(modelId, out _);
        if (kind == NLPCloudModelKind.Translation)
            return await TranslateSamplingAsync(chatRequest, modelId, cancellationToken);

        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => TranscriptionRequestInternal(request, cancellationToken);

    // SpeechRequest implemented in NLPCloudProvider.Speech.cs

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
