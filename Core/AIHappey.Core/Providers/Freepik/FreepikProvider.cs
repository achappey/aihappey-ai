using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.Freepik;

public sealed partial class FreepikProvider : IModelProvider
{
    private readonly HttpClient _client;
    private readonly IApiKeyResolver _keyResolver;

    public FreepikProvider(HttpClient client, IApiKeyResolver keyResolver)
    {
        _client = client;
        _keyResolver = keyResolver;
    }

    public string GetIdentifier() => nameof(Freepik).ToLowerInvariant();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        switch (model?.Type)
        {
            case "speech":
                {
                    return await this.SpeechSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }

            case "image":
                {
                    return await this.ImageSamplingAsync(chatRequest,
                            cancellationToken: cancellationToken);
                }


            default:
                throw new NotImplementedException();
        }
    }

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => ModelProviderImageExtensions.StreamImageAsync(this, chatRequest, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => SoundEffectsSpeechRequest(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    private string GetKey() => _keyResolver.Resolve(GetIdentifier()) ?? throw new InvalidOperationException("No Freepik API key configured.");

    private void ApplyAuthHeader()
    {
        _client.DefaultRequestHeaders.Remove("x-freepik-api-key");
        _client.DefaultRequestHeaders.Add("x-freepik-api-key", GetKey());
        _client.DefaultRequestHeaders.Accept.Clear();
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }
}

