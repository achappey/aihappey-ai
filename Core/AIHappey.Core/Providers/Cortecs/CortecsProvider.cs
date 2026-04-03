using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.Cortecs;

public partial class CortecsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public CortecsProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.cortecs.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Cortecs)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Cortecs).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.GetModel(), cancellationToken);

        return (model?.Type) switch
        {
            "language" => await this.ChatCompletionsSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            "transcription" => await this.TranscriptionSamplingAsync(chatRequest,
                                    cancellationToken: cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => this.TranscriptionRequestInternal(imageRequest, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private async Task<CreateMessageResult> TranscriptionSamplingAsync(
        CreateMessageRequestParams chatRequest,
        CancellationToken cancellationToken = default)
    {
        var model = chatRequest.GetModel();
        if (string.IsNullOrWhiteSpace(model))
            throw new Exception("No model provided.");

        var audio = chatRequest.Messages
            .Where(a => a.Role == ModelContextProtocol.Protocol.Role.User)
            .SelectMany(a => a.Content.OfType<AudioContentBlock>())
            .LastOrDefault();

        if (audio is null)
            throw new Exception("No audio input provided.");

        var request = new TranscriptionRequest
        {
            Model = model,
            MediaType = audio.MimeType,
            Audio = Convert.ToBase64String(audio.Data.ToArray()),
            ProviderOptions = chatRequest.Metadata?
                .ToDictionary(
                    kvp => kvp.Key,
                    kvp => JsonSerializer.SerializeToElement(kvp.Value)
                )
        };

        var result = await this.TranscriptionRequest(request, cancellationToken)
            ?? throw new Exception("No result.");

        return new CreateMessageResult
        {
            Model = result.Response?.ModelId ?? model,
            StopReason = "stop",
            Content = [(result.Text ?? string.Empty).ToTextContentBlock()],
            Role = ModelContextProtocol.Protocol.Role.Assistant
        };
    }

    public Task<JsonElement> MessagesAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<JsonElement> MessagesStreamingAsync(JsonElement request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
