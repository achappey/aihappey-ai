using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Telnyx;

public partial class TelnyxProvider
    : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;
    public TelnyxProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.telnyx.com/v2/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Telnyx)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => nameof(Telnyx).ToLowerInvariant();

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetChatCompletion(
             options,
             relativeUrl: "ai/chat/completions",
             ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options,
                    relativeUrl: "ai/chat/completions",
                    ct: cancellationToken);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        using var req = new HttpRequestMessage(HttpMethod.Get, "ai/models");
        using var resp = await _client.SendAsync(req, cancellationToken);

        var body = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Telnyx models failed ({(int)resp.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);

        // { object: "list", data: [ { id, created, owned_by } ] }
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            return [];

        var models = new List<Model>();

        foreach (var el in data.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var fullId = id!.ToModelId(GetIdentifier());

            models.Add(new Model
            {
                Id = fullId,
                Name = id!,
                OwnedBy = el.TryGetProperty("owned_by", out var ob) ? (ob.GetString() ?? "") : "",
                Created = el.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.Number
                    ? created.GetInt64()
                    : null,
                Type = fullId.GuessModelType()
            });
        }

        if (!models.Any(a => a.Id.EndsWith("distil-whisper/distil-large-v2")))
            models.Add(new()
            {
                Id = "distil-whisper/distil-large-v2".ToModelId(GetIdentifier()),
                Name = "distil-large-v2",
                Type = "transcription"
            });

        if (!models.Any(a => a.Id.EndsWith("openai/whisper-large-v3-turbo")))
            models.Add(new()
            {
                Id = "openai/whisper-large-v3-turbo".ToModelId(GetIdentifier()),
                Name = "whisper-large-v3-turbo",
                Type = "transcription"
            });

        return models;
    }
}

