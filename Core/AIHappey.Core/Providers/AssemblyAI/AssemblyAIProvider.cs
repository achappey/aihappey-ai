using ModelContextProtocol.Protocol;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AssemblyAI;

public partial class AssemblyAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    // Speech-to-Text / Realtime / Audio Intelligence
    private readonly HttpClient _client;

    // LLM Gateway (OpenAI-compatible chat completions)
    private readonly HttpClient _llmGatewayClient;

    public AssemblyAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.assemblyai.com/");

        _llmGatewayClient = httpClientFactory.CreateClient();
        _llmGatewayClient.BaseAddress = new Uri("https://llm-gateway.assemblyai.com/");
    }

    private void ApplyAuthHeader()
    {
        ApplyAuthHeader(_client);
    }

    private void ApplyAuthHeader(HttpClient client)
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(AssemblyAI)} API key.");

        client.DefaultRequestHeaders.Remove("Authorization");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", key);
    }


    public string GetIdentifier() => nameof(AssemblyAI).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        return (model?.Type) switch
        {
            "language" => await this.ChatCompletionsSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}


