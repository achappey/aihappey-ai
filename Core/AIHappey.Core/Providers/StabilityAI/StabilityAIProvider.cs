using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model.Responses;

namespace AIHappey.Core.Providers.StabilityAI;

public partial class StabilityAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public StabilityAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.stability.ai/v2beta/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(StabilityAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(StabilityAI).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("audio") == true)
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken: cancellationToken))
            yield return update;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }


    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (options.Model?.Contains("audio") == true)
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
