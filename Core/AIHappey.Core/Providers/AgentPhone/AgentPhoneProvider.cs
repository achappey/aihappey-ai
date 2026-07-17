using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.AgentPhone;

public partial class AgentPhoneProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public AgentPhoneProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.agentphone.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(AgentPhone)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }


    public string GetIdentifier() => nameof(AgentPhone).ToLowerInvariant();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();


    public async Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest,
     CancellationToken cancellationToken)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Post, "v1/calls/web")
        {
            Content = JsonContent.Create(new
            {
                agentId = realtimeRequest.Model
            })
        };

        using var resp = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var contents = await resp.Content.ReadAsStringAsync(cancellationToken);

            throw new Exception(contents);
        }

        var token = await resp.Content.ReadFromJsonAsync<WebCallResponse>(cancellationToken)
            ?? throw new Exception();

        return new RealtimeResponse()
        {
            Value = token.AccessToken,
            ExpiresAt = DateTimeOffset.UtcNow
                .AddSeconds(30)
                .ToUnixTimeSeconds(),
        };
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
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

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

public class WebCallResponse
{
    [JsonPropertyName("accessToken")]
    public string AccessToken { get; set; } = null!;

}
