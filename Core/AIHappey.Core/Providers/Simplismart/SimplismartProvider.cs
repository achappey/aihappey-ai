using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using AIHappey.Unified.Models;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using AIHappey.Core.Extensions;

namespace AIHappey.Core.Providers.Simplismart;

public partial class SimplismartProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public SimplismartProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.simplismart.live/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Simplismart)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options,
             relativeUrl: "chat/completions",
             cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options,
                    relativeUrl: "chat/completions",
                    cancellationToken: cancellationToken);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
       => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public string GetIdentifier() => nameof(Simplismart).ToLowerInvariant();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
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

    public async Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await this.IsTranscriptionModelAsync(request.Model, cancellationToken))
            return await this.ExecuteUnifiedTranscriptionAsync(request, cancellationToken);
        return await this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stream = await this.IsTranscriptionModelAsync(request.Model, cancellationToken)
            ? this.StreamUnifiedTranscriptionAsync(request, cancellationToken)
            : this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
        await foreach (var item in stream.WithCancellation(cancellationToken)) yield return item;
    }

    private static Dictionary<string, object?> SimplismartCreatePayload(JsonElement metadata)
    {
        var payload = new Dictionary<string, object?>();
        if (metadata.ValueKind == JsonValueKind.Object)
            foreach (var property in metadata.EnumerateObject()) payload[property.Name] = property.Value.Clone();
        return payload;
    }

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SimplismartPostJsonAsync(
        string endpoint, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, MediaTypeNames.Application.Json)
        };
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SimpliSmart request failed ({(int)response.StatusCode} {response.ReasonPhrase}): {raw}");
        using var document = JsonDocument.Parse(raw);
        return (document.RootElement.Clone(), response.GetHeaders());
    }

    private static string[] SimplismartReadStringArray(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : [];

    private static string? SimplismartReadString(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    private static float? SimplismartReadFloat(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetSingle(out var number)
            ? number : null;

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
