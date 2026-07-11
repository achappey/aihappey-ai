using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Common.Extensions;
using AIHappey.Responses.Streaming;
using AIHappey.Responses;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Runway;

public partial class RunwayProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public RunwayProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.dev.runwayml.com/");
        _client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(MediaTypeNames.Application.Json));
        _client.DefaultRequestHeaders.Add("X-Runway-Version", "2024-11-06");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Runway)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        var models = (await this.ListModels(_keyResolver.Resolve(GetIdentifier()))).ToList();

        try
        {
            ApplyAuthHeader();
            models.AddRange(await ListCustomAvatarModelsAsync(cancellationToken));
        }
        catch
        {
            // Keep model listing resilient: custom avatars are additive and require a valid Runway key.
        }

        return models
            .GroupBy(static model => model.Id, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToList();
    }

    private async Task<IEnumerable<Model>> ListCustomAvatarModelsAsync(CancellationToken cancellationToken)
    {
        List<Model> models = [];
        string? cursor = null;

        do
        {
            var uri = string.IsNullOrWhiteSpace(cursor)
                ? "v1/avatars"
                : $"v1/avatars?cursor={Uri.EscapeDataString(cursor)}";

            using var response = await _client.GetAsync(uri, cancellationToken);
            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Runway avatar listing failed ({(int)response.StatusCode}): {raw}");

            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                break;

            foreach (var avatar in data.EnumerateArray())
            {
                var id = avatar.TryGetString("id");
                if (string.IsNullOrWhiteSpace(id))
                    continue;

                var name = avatar.TryGetString("name") ?? id;
                var status = avatar.TryGetString("status");
                var voiceName = TryGetAvatarVoiceName(avatar);

                models.Add(new Model
                {
                    Id = $"avatar/{id}".ToModelId(GetIdentifier()),
                    Name = $"Avatar: {name}",
                    OwnedBy = "Runway",
                    Type = "video",
                    Created = TryGetUnixTimeSeconds(avatar.TryGetString("createdAt")),
                    Description = string.IsNullOrWhiteSpace(voiceName)
                        ? $"Runway custom avatar '{name}'{FormatAvatarStatus(status)}."
                        : $"Runway custom avatar '{name}' using voice '{voiceName}'{FormatAvatarStatus(status)}.",
                    Tags = BuildAvatarModelTags(status)
                });
            }

            cursor = root.TryGetString("nextCursor");
            var hasMore = root.TryGetProperty("hasMore", out var hasMoreEl)
                          && hasMoreEl.ValueKind == JsonValueKind.True;

            if (!hasMore)
                cursor = null;
        } while (!string.IsNullOrWhiteSpace(cursor));

        return models;
    }

    private static string? TryGetAvatarVoiceName(JsonElement avatar)
    {
        if (!avatar.TryGetProperty("voice", out var voice) || voice.ValueKind != JsonValueKind.Object)
            return null;

        return voice.TryGetString("name") ?? voice.TryGetString("presetId") ?? voice.TryGetString("id");
    }

    private static string FormatAvatarStatus(string? status)
        => string.IsNullOrWhiteSpace(status) ? string.Empty : $" ({status})";

    private static IEnumerable<string> BuildAvatarModelTags(string? status)
    {
        List<string> tags = ["persona"];

        return tags;
    }

    private static long? TryGetUnixTimeSeconds(string? value)
        => DateTimeOffset.TryParse(value, out var timestamp)
            ? timestamp.ToUnixTimeSeconds()
            : null;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => "runway";

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
      throw new NotSupportedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
       ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken);

        switch (model?.Type)
        {
            case "speech":
                {
                    await foreach (var update in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return update;

                    yield break;
                }

            case "image":
                {
                    await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken))
                        yield return update;

                    yield break;
                }

            case "video":
                {
                    await foreach (var update in this.StreamVideoAsync(chatRequest, cancellationToken))
                        yield return update;

                    yield break;
                }

            default:
                throw new NotImplementedException();
        }
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => string.Equals(request?.Model, "eleven_text_to_sound_v2", StringComparison.OrdinalIgnoreCase)
            ? RunwaySoundEffectAsync(request!, cancellationToken)
            : RunwayTextToSpeechAsync(request!, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
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
}
