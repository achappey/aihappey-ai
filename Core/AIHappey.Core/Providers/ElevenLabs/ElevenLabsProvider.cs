using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using OpenAI.Responses;
using OAIC = OpenAI.Chat;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    : IModelProvider
{
    private readonly HttpClient _client = httpClientFactory.CreateClient();

    public string GetIdentifier() => "elevenlabs";

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No ElevenLabs API key.");

        _client.BaseAddress ??= new Uri("https://api.elevenlabs.io/");

        _client.DefaultRequestHeaders.Remove("xi-api-key");
        _client.DefaultRequestHeaders.Add("xi-api-key", key);
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ModelContextProtocol.Protocol.CreateMessageResult> SamplingAsync(ModelContextProtocol.Protocol.CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("scribe") == true)
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

