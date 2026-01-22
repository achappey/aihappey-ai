using ModelContextProtocol.Protocol;
using AIHappey.Core.Models;
using AIHappey.Common.Model;
using AIHappey.Core.ModelProviders;

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
    {
        ApplyAuthHeader();

        return AssemblyAIAllModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

}


