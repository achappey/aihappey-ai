using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Vercel.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.DeAPI;

public partial class DeAPIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions DeapiJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public DeAPIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.deapi.ai/");
    }

    public string GetIdentifier() => "deapi";

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No DeAPI API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await ListModelsDeapi(cancellationToken);

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => DeapiImageRequest(request, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => DeapiTranscriptionRequest(request, cancellationToken);

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => DeapiSpeechRequest(request, cancellationToken);

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
        => DeapiVideoRequest(request, cancellationToken);

    private async Task<string> SubmitJsonJobAsync(string endpoint, object payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, DeapiJson);
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(json, Encoding.UTF8, MediaTypeNames.Application.Json)
        };

        using var resp = await _client.SendAsync(req, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeAPI request failed ({(int)resp.StatusCode}): {raw}");

        return ExtractRequestId(raw);
    }

    private async Task<string> SubmitMultipartJobAsync(string endpoint, MultipartFormDataContent form, CancellationToken cancellationToken)
    {
        using var resp = await _client.PostAsync(endpoint, form, cancellationToken);
        var raw = await resp.Content.ReadAsStringAsync(cancellationToken);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeAPI request failed ({(int)resp.StatusCode}): {raw}");

        return ExtractRequestId(raw);
    }

    private static string ExtractRequestId(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var requestId = root.TryGetProperty("data", out var dataEl)
                        && dataEl.ValueKind == JsonValueKind.Object
                        && dataEl.TryGetProperty("request_id", out var idEl)
                        && idEl.ValueKind == JsonValueKind.String
            ? idEl.GetString()
            : root.TryGetProperty("request_id", out var directIdEl) && directIdEl.ValueKind == JsonValueKind.String
                ? directIdEl.GetString()
                : null;

        if (string.IsNullOrWhiteSpace(requestId))
            throw new InvalidOperationException("DeAPI response contained no request_id.");

        return requestId;
    }

    private async Task<JsonElement> WaitForJobResultAsync(string requestId, CancellationToken cancellationToken)
    {
        var terminal = await AsyncTaskPollingExtensions.PollUntilTerminalAsync(
            async ct =>
            {
                using var pollReq = new HttpRequestMessage(HttpMethod.Get, $"api/v1/client/request-status/{requestId}");
                using var pollResp = await _client.SendAsync(pollReq, ct);
                var pollRaw = await pollResp.Content.ReadAsStringAsync(ct);
                if (!pollResp.IsSuccessStatusCode)
                    throw new InvalidOperationException($"DeAPI status poll failed ({(int)pollResp.StatusCode}): {pollRaw}");

                using var doc = JsonDocument.Parse(pollRaw);
                var root = doc.RootElement;
                if (!root.TryGetProperty("data", out var dataEl) || dataEl.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("DeAPI status payload does not include data.");

                return dataEl.Clone();
            },
            isTerminal: data =>
            {
                var status = data.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                    ? s.GetString()
                    : null;

                return string.Equals(status, "done", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(status, "error", StringComparison.OrdinalIgnoreCase);
            },
            interval: TimeSpan.FromSeconds(2),
            timeout: TimeSpan.FromMinutes(10),
            maxAttempts: null,
            cancellationToken: cancellationToken);

        var terminalStatus = terminal.TryGetProperty("status", out var statusEl) && statusEl.ValueKind == JsonValueKind.String
            ? statusEl.GetString()
            : null;

        if (string.Equals(terminalStatus, "error", StringComparison.OrdinalIgnoreCase))
        {
            var error = terminal.TryGetProperty("error", out var errorEl)
                ? errorEl.ToString()
                : "Unknown DeAPI error";

            throw new InvalidOperationException($"DeAPI job failed ({requestId}): {error}");
        }

        return terminal;
    }

    private static string? GetResultUrl(JsonElement data)
    {
        if (data.TryGetProperty("result_url", out var directUrlEl) && directUrlEl.ValueKind == JsonValueKind.String)
            return directUrlEl.GetString();

        if (data.TryGetProperty("result", out var resultEl) && resultEl.ValueKind == JsonValueKind.Object
            && resultEl.TryGetProperty("result_url", out var nestedUrlEl) && nestedUrlEl.ValueKind == JsonValueKind.String)
            return nestedUrlEl.GetString();

        return null;
    }

    private async Task<(byte[] bytes, string mediaType)> DownloadResultAsync(string url, string fallbackMediaType, CancellationToken cancellationToken)
    {
        using var downloadResp = await _client.GetAsync(url, cancellationToken);
        var bytes = await downloadResp.Content.ReadAsByteArrayAsync(cancellationToken);
        if (!downloadResp.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeAPI result download failed ({(int)downloadResp.StatusCode}).");

        var mediaType = downloadResp.Content.Headers.ContentType?.MediaType ?? fallbackMediaType;
        return (bytes, mediaType);
    }

    private static string? ExtractResultText(JsonElement data)
    {
        if (data.TryGetProperty("result", out var resultEl))
        {
            if (resultEl.ValueKind == JsonValueKind.String)
                return resultEl.GetString();

            if (resultEl.ValueKind == JsonValueKind.Object)
            {
                if (resultEl.TryGetProperty("text", out var textEl) && textEl.ValueKind == JsonValueKind.String)
                    return textEl.GetString();

                if (resultEl.TryGetProperty("transcription", out var trEl) && trEl.ValueKind == JsonValueKind.String)
                    return trEl.GetString();
            }
        }

        return null;
    }

    private static double? TryGetNumber(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var valueEl))
            return null;

        if (valueEl.ValueKind == JsonValueKind.Number && valueEl.TryGetDouble(out var n))
            return n;

        if (valueEl.ValueKind == JsonValueKind.String
            && double.TryParse(valueEl.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed))
            return parsed;

        return null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        if (!element.TryGetProperty(propertyName, out var valueEl))
            return null;

        if (valueEl.ValueKind == JsonValueKind.True)
            return true;
        if (valueEl.ValueKind == JsonValueKind.False)
            return false;

        if (valueEl.ValueKind == JsonValueKind.String
            && bool.TryParse(valueEl.GetString(), out var parsed))
            return parsed;

        return null;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;

        return element.TryGetProperty(propertyName, out var valueEl) && valueEl.ValueKind == JsonValueKind.String
            ? valueEl.GetString()
            : null;
    }
}

