using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.SmallestAI;

public partial class SmallestAIProvider : IModelProvider
{
    private const string ProviderId = "smallestai";
    private const string ProviderName = "SmallestAI";

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public SmallestAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://waves-api.smallest.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await ListModelsInternal(cancellationToken);

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);
        var type = model.Type;

        if (string.Equals(type, "speech", StringComparison.OrdinalIgnoreCase))
            return await this.SpeechSamplingAsync(chatRequest, cancellationToken);

        throw new NotSupportedException($"{ProviderName} sampling supports speech models only.");
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);
        var type = model.Type;

        if (string.Equals(type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => await this.SpeechResponseAsync(options, cancellationToken);

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}

