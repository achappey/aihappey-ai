using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Reve;

public partial class ReveProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ReveProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.reve.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Reve)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(Reve).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => await this.ImageSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

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

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in this.StreamImageAsync(chatRequest,
                          cancellationToken: cancellationToken))
            yield return update;

        yield break;
    }
}