using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.Extensions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.ElevenLabs;

public partial class ElevenLabsProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory, AsyncCacheHelper asyncCacheHelper)
    : IModelProvider, IUnifiedModelProvider
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

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var update in StreamUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            foreach (var part in update.Event.ToUIMessagePart(GetIdentifier()))
                yield return part;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var update in StreamUnifiedAsync(
                                 request,
                                  cancellationToken: cancellationToken))
        {
            yield return update.ToChatCompletionUpdate();
        }
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options,
     [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var request = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var update in StreamUnifiedAsync(
                           request,
                           cancellationToken: cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return update;
    }

    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        var payload = JsonSerializer.SerializeToElement(new { });
        var resp = await _client.GetRealtimeResponse<ElevenLabsTokenResponse>(payload,
            relativeUrl: "v1/single-use-token/realtime_scribe",
            ct: cancellationToken);

        return new RealtimeResponse()
        {
            Value = resp.Token,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeSeconds(),
        };
    }



    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("scribe", StringComparison.OrdinalIgnoreCase) == true
            ? this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken)
            : this.ExecuteUnifiedSpeechAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("scribe", StringComparison.OrdinalIgnoreCase) == true
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
            : this.StreamUnifiedSpeechAsync(request, cancellationToken);

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
          [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken: cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }


    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}


public class ElevenLabsTokenResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = null!;

}
