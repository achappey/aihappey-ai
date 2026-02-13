using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using System.Text.Json;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Common.Model;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Hyperstack;

public partial class HyperstackProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public HyperstackProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://console.hyperstack.cloud/ai/api/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Hyperstack)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["temperature"] = options.Temperature,
            ["messages"] = options.Messages.ToHyperstackMessages(),
            ["stream"] = false
        };

        var json = await HyperstackCompletionAsync(payload, cancellationToken);

        var result = JsonSerializer.Deserialize<ChatCompletion>(json, JsonSerializerOptions.Web);

        if (result is null)
            throw new InvalidOperationException("Empty JSON response from Hyperstack chat completions.");

        return result;
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));


    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var payload = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["temperature"] = options.Temperature,
            ["messages"] = options.Messages.ToHyperstackMessages(),
            ["stream"] = true
        };

        await foreach (var data in HyperstackStreamAsync(payload, cancellationToken))
        {
            ChatCompletionUpdate? update;
            try
            {
                update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, JsonSerializerOptions.Web);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse Hyperstack SSE json event: {data}", ex);
            }

            if (update is not null)
                yield return update;
        }
    }

    public string GetIdentifier() => nameof(Hyperstack).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => await this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);

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

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
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
}
