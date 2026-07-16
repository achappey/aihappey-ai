using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Messages;
using System.Text.Json;
using System.Text;
using AIHappey.ChatCompletions.Models;
using Microsoft.AspNetCore.Http;
using AIHappey.Vercel.Models;
using AIHappey.Common.Model;
using AIHappey.Core.Contracts;

namespace AIHappey.Core.Providers.Pollinations;

public partial class PollinationsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IHttpContextAccessor _context;
    private readonly HttpClient _client;

    public PollinationsProvider(IApiKeyResolver keyResolver,
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _context = httpContextAccessor;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://text.pollinations.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }



    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(Pollinations).ToLowerInvariant();

    private readonly ModelPricing Free = new()
    {
        Input = 0,
        Output = 0
    };

    public Task<CreateMessageResult> SamplingAsync(
     CreateMessageRequestParams chatRequest,
     CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }


    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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
}