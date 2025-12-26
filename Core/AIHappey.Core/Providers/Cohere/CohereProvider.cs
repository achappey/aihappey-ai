using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using System.Text.Json;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Cohere;

public partial class CohereProvider : IModelProvider
{
    private readonly HttpClient _client;

    private readonly IApiKeyResolver _keyResolver;

    public CohereProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _client = httpClientFactory.CreateClient();
        _keyResolver = keyResolver;
        _client.BaseAddress = new Uri("https://api.cohere.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Cohere)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => CohereExtensions.CohereIdentifier;

    public float? GetPriority() => 1;

    public async Task<IEnumerable<Model>> ListModels(
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models?page_size=1000");
        using var response = await _client.SendAsync(request, cancellationToken);

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(payload);
        if (!doc.RootElement.TryGetProperty("models", out var modelsEl)
            || modelsEl.ValueKind != JsonValueKind.Array)
            return [];

        var result = new List<Model>();

        foreach (var item in modelsEl.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var nameEl)
                ? nameEl.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(name)) continue;

            DateTimeOffset? createdAt = null;
            if (item.TryGetProperty("created_at", out var createdEl)
                && createdEl.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(createdEl.GetString(), out var dt))
            {
                createdAt = dt;
            }

            result.Add(new Model
            {
                Id = name!.ToModelId(GetIdentifier()),
                Name = name!,
                //Publisher = nameof(Cohere),
                OwnedBy = nameof(Cohere),
                Created = createdAt?.ToUnixTimeSeconds()
            });
        }

        return result;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}