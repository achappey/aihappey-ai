using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using System.Runtime.CompilerServices;
using System.Text.Json;

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
        var relativeUrl = await GetChatCompletionsRelativeUrlAsync(cancellationToken);
        return await CompleteChatCustomAsync(options, relativeUrl, cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var relativeUrl = await GetChatCompletionsRelativeUrlAsync(cancellationToken);

        await foreach (var update in CompleteChatStreamingCustomAsync(options, relativeUrl, cancellationToken))
        {
            yield return update;
        }
    }

    public string GetIdentifier() => nameof(Infomaniak).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
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
}
