using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.ARKLabs;

public partial class ARKLabsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ARKLabsProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.ark-labs.cloud/api/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(ARKLabs)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(ARKLabs).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        return (model?.Type) switch
        {
            "image" => await this.ImageSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            "language" => await this.ChatCompletionsSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => TranscriptionRequestInternal(imageRequest, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
