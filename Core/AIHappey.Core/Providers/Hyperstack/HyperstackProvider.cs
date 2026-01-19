using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using System.Text.Json;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;

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

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
