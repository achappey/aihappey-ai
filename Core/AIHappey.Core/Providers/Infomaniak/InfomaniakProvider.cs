using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Unified.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Infomaniak;

public partial class InfomaniakProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public InfomaniakProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.infomaniak.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Infomaniak)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!options.Tools.Any())
            options.Tools = null!;

        var relativeUrl = await GetChatCompletionsRelativeUrlAsync(cancellationToken);

        return await this.GetChatCompletion(_client,
            options,
            relativeUrl,
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!options.Tools.Any())
            options.Tools = null!;

        var relativeUrl = await GetChatCompletionsRelativeUrlAsync(cancellationToken);

        await foreach (var update in this.GetChatCompletions(_client, options, relativeUrl, cancellationToken: cancellationToken))
        {
            yield return update;
        }

    }

    public string GetIdentifier() => nameof(Infomaniak).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
         Responses.ResponseRequest options,
         CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private async Task<string> GetChatCompletionsRelativeUrlAsync(CancellationToken cancellationToken = default)
    {
        var productId = await GetProductIdAsync(cancellationToken);
        return $"2/ai/{productId}/openai/v1/chat/completions";
    }

    private async Task<int> GetProductIdAsync(CancellationToken cancellationToken = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "1/ai");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Infomaniak /1/ai failed with {(int)resp.StatusCode} {resp.ReasonPhrase}: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = doc.RootElement;

        if (!root.TryGetProperty("result", out var resultEl)
            || resultEl.ValueKind != JsonValueKind.String
            || !string.Equals(resultEl.GetString(), "success", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Infomaniak /1/ai returned a non-success result.");
        }

        if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Infomaniak /1/ai returned an invalid payload: missing data array.");

        foreach (var product in dataEl.EnumerateArray())
        {
            if (!product.TryGetProperty("status", out var statusEl)
                || statusEl.ValueKind != JsonValueKind.String
                || !string.Equals(statusEl.GetString(), "ok", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (product.TryGetProperty("product_id", out var productIdEl)
                && productIdEl.ValueKind == JsonValueKind.Number
                && productIdEl.TryGetInt32(out var productId)
                && productId > 0)
            {
                return productId;
            }

            throw new InvalidOperationException("Infomaniak /1/ai returned an ok product without a valid product_id.");
        }

        throw new InvalidOperationException("Infomaniak /1/ai returned no product with status 'ok'.");
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            foreach (var item in part.ToMessageStreamParts())
                yield return item;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
     => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

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
