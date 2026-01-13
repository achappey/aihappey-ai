using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace AIHappey.Core.Providers.Nvidia;

/// <summary>
/// NVIDIA NIM for LLMs (OpenAI-compatible Chat Completions endpoint).
/// Default base URL: https://integrate.api.nvidia.com/
/// Endpoint: POST /v1/chat/completions
/// </summary>
public partial class NvidiaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://integrate.api.nvidia.com/");
        return client;
    }

    public string GetIdentifier() => nameof(Nvidia).ToLowerInvariant();

    private void ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No NVIDIA API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "v1/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        if (!resp.IsSuccessStatusCode)
        {
            var err = await resp.Content.ReadAsStringAsync(cancellationToken);
            throw new Exception($"NVIDIA API error: {err}");
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = doc.RootElement;
        var data = root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Array
            ? dataEl
            : default;

        if (data.ValueKind != JsonValueKind.Array)
            return [];

        return [.. data
            .EnumerateArray()
            .Select(m =>
            {
                var id = m.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                var created = m.TryGetProperty("created", out var cEl) && cEl.ValueKind == JsonValueKind.Number
                    ? cEl.GetInt64()
                    : 0;
                var ownedBy = m.TryGetProperty("owned_by", out var oEl) ? oEl.GetString() : null;

                return new Model
                {
                    Id = (id ?? string.Empty).ToModelId(GetIdentifier()),
                    Name = id ?? string.Empty,
                    OwnedBy = ownedBy ?? "NVIDIA",
                    Created = created,
                    Type = "language",
                };
            })
            .Where(m => !string.IsNullOrWhiteSpace(m.Id))
            .DistinctBy(m => m.Id)
            .OrderByDescending(m => m.Created)];
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        // NVIDIA NIM for LLMs is OpenAI Chat Completions compatible.
        // Default endpoint: POST https://integrate.api.nvidia.com/v1/chat/completions
        await foreach (var update in _client.CompletionsStreamAsync(
            chatRequest,
            url: "v1/chat/completions",
            cancellationToken: cancellationToken))
        {
            yield return update;
        }
    }

    // ChatCompletions endpoint is not used by the Vercel UI stream (`/api/chat`).
    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

