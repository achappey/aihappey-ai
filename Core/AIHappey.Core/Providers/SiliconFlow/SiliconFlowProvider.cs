using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.SiliconFlow;

public partial class SiliconFlowProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;
    private readonly IHttpClientFactory _factory;

    public SiliconFlowProvider(IApiKeyResolver keyResolver,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _factory = httpClientFactory;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.siliconflow.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(SiliconFlow)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(SiliconFlow).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        switch (model?.Type)
        {
            case "image":
                {
                    return await this.ImageSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }

            case "speech":
                {
                    return await this.SpeechSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }

            default:
                throw new NotImplementedException();
        }
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        switch (model?.Type)
        {
            case "speech":
                {
                    return await this.SpeechResponseAsync(options,
                            cancellationToken: cancellationToken);
                }

            default:
                throw new NotImplementedException();
        }
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
