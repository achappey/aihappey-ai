using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
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

        return (model?.Type) switch
        {
            "image" => await this.ImageSamplingAsync(chatRequest,
                                        cancellationToken: cancellationToken),
            "speech" => await this.SpeechSamplingAsync(chatRequest,
                                        cancellationToken: cancellationToken),
            "language" => await this.ChatCompletionsSamplingAsync(chatRequest,
                                        cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);

        return (model?.Type) switch
        {
            "speech" => await this.SpeechResponseAsync(options,
                                        cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();
}
