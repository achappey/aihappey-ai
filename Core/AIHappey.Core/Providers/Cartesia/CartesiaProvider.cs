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

namespace AIHappey.Core.Providers.Cartesia;

public partial class CartesiaProvider : IModelProvider
{
    private const string ProviderId = "cartesia";
    private const string ProviderName = "Cartesia";
    private const string DefaultApiVersion = "2025-04-16";

    private static readonly string[] SupportedTtsModelIds =
    [
        "sonic-3-2026-01-12",
        "sonic-3-2025-10-27",
        "sonic-3",
        "sonic-3-latest",
        "sonic-2-2025-06-11",
        "sonic-2-2025-05-08",
        "sonic-2-2025-04-16",
        "sonic-2-2025-03-07",
        "sonic-turbo-2025-03-07",
        "sonic-2024-12-12",
        "sonic-2024-10-19"
    ];

    private static readonly string[] SupportedTranscriptionModelIds =
    [
        "ink-whisper",
        "ink-whisper-2025-06-04"
    ];

    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public CartesiaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.cartesia.ai/");
    }

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {ProviderName} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    private static void ApplyVersionHeader(HttpRequestMessage request, string? apiVersion)
    {
        request.Headers.Remove("Cartesia-Version");
        request.Headers.TryAddWithoutValidation("Cartesia-Version", string.IsNullOrWhiteSpace(apiVersion) ? DefaultApiVersion : apiVersion.Trim());
    }

    private bool IsTranscriptionModel(string model)
    {
        var normalized = model;
        if (normalized.StartsWith("transcription/", StringComparison.OrdinalIgnoreCase))
            return true;

        return SupportedTranscriptionModelIds.Any(m => string.Equals(m, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await ListModelsInternal(cancellationToken);

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => this.SpeechSamplingAsync(chatRequest, cancellationToken);

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (IsTranscriptionModel(chatRequest.Model))
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        if (IsTranscriptionModel(options.Model!))
            throw new NotSupportedException("Cartesia transcription is not supported on Responses API.");

        return await this.SpeechResponseAsync(options, cancellationToken);
    }

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

