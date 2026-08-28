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

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => options.Model.Contains("scribe", StringComparison.OrdinalIgnoreCase)
            ? (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion()
            : throw new NotImplementedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("scribe") == true)
        {
            var request = chatRequest.ToUnifiedRequest(GetIdentifier());

            await foreach (var update in StreamUnifiedAsync(request, cancellationToken))
            {
                foreach (var part in update.Event.ToUIMessagePart(GetIdentifier()))
                    yield return part;
            }

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

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (options.Model?.Contains("scribe") == true)
        {
            return (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToResponseResult();
        }

        return await this.SpeechResponseAsync(options, cancellationToken);
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
        => request.Model?.Contains("scribe", StringComparison.OrdinalIgnoreCase) == true
            ? (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse()
            : throw new NotImplementedException();

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("scribe", StringComparison.OrdinalIgnoreCase) == true
            ? this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken)
            : throw new NotSupportedException($"ElevenLabs unified model '{request.Model}' is not supported.");

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => request.Model?.Contains("scribe", StringComparison.OrdinalIgnoreCase) == true
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
            : throw new NotSupportedException($"ElevenLabs unified model '{request.Model}' is not supported.");

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
          [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var update in StreamUnifiedAsync(
                                 unifiedRequest,
                                  cancellationToken: cancellationToken))
        {
            foreach (var result in update.ToMessageStreamParts())
                yield return result;
        }
    }


    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }



    public async Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        options.ValidateOpenAITranscriptionRequest();
        var responseFormat = options.ResolveOpenAITranscriptionResponseFormat();
        var request = await options.ToTranscriptionRequest(options.Model, GetIdentifier(), cancellationToken);
        var response = await TranscriptionRequest(request, cancellationToken);
        return response.ToOpenAITranscriptionResponse(responseFormat);
    }

    public async IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await OpenAITranscriptionRequestAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.IsNullOrWhiteSpace(response.Text))
            yield return new OpenAITranscriptionTextDelta { Delta = response.Text };

        yield return new OpenAITranscriptionTextDone { Text = response.Text };
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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
