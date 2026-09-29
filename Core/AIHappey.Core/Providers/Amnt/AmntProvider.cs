using System.Net.Http.Headers;
using System.Net.Mime;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Amnt;

public partial class AmntProvider : IModelProvider, IUnifiedModelProvider
{
    private readonly IApiKeyResolver _keys;
    private readonly AsyncCacheHelper _cache;
    private readonly HttpClient _client;
    private static readonly JsonSerializerOptions Json = JsonSerializerOptions.Web;

    public AmntProvider(IApiKeyResolver keys, AsyncCacheHelper cache, IHttpClientFactory factory)
    {
        _keys = keys;
        _cache = cache;
        _client = factory.CreateClient();
        _client.BaseAddress = new Uri("https://www.amnt.io/");
        // SVG operations may run for 300 seconds. Never retry a timed-out paid call.
        _client.Timeout = TimeSpan.FromSeconds(310);
    }

    public string GetIdentifier() => "amnt";

    private async Task<(JsonElement Body, IDictionary<string, string> Headers)> SendAsync(
        HttpMethod method, string path, object? payload, CancellationToken ct)
    {
        var key = _keys.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("No Amnt API key.");
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (payload is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, MediaTypeNames.Application.Json);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonElement body;
        try { using var document = JsonDocument.Parse(text); body = document.RootElement.Clone(); }
        catch (JsonException ex) { throw new HttpRequestException($"Amnt returned invalid JSON (HTTP {(int)response.StatusCode}).", ex, response.StatusCode); }
        if (!response.IsSuccessStatusCode)
        {
            var detail = String(body, "error") ?? String(body, "message") ?? "Request failed";
            var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds;
            throw new HttpRequestException($"Amnt HTTP {(int)response.StatusCode}: {detail}"
                + (retry is null ? "" : $" (Retry-After: {retry:0} seconds)"), null, response.StatusCode);
        }
        return (body, response.Headers.Concat(response.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase));
    }

    private static string? String(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field)
            && field.ValueKind == JsonValueKind.String ? field.GetString() : null;

    private static JsonElement? Property(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field.Clone() : null;

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken ct = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), ct)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), ct))
            yield return part.ToChatCompletionUpdate();
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken ct = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), ct)).ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), ct).ToResponseStreamParts(ct))
            yield return part;
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken ct = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), ct)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request, Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), ct).ToMessageStreamParts(request.Model, ct))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), ct))
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier())) yield return uiPart;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken ct = default) => throw new NotSupportedException();
}
