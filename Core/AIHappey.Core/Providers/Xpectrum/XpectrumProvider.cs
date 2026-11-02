using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Providers.OpenAI.Live;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Vercel.Models;
using AIHappey.Responses;

namespace AIHappey.Core.Providers.Xpectrum;

/// <summary>
/// Xpectrum app API. One API key is scoped to exactly one app (agent); the app type
/// decides whether chat completions (chatbot, agent, flow) or workflow runs apply.
/// </summary>
public sealed partial class XpectrumProvider(
    IApiKeyResolver keyResolver,
    IEndUserIdResolver endUserIdResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper memoryCache) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://cloud.xpectrum.dev/v1/");
        return client;
    }

    public string GetIdentifier() => "xpectrum";

    private string ApplyAuthHeader()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Xpectrum API key.");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return key;
    }

    private static string NativeModel(string? model)
        => model?.StartsWith("xpectrum/", StringComparison.Ordinal) == true ? model["xpectrum/".Length..] : model ?? "";

    private static Dictionary<string, object?> CopyObject(JsonElement options)
        => options.ValueKind switch
        {
            JsonValueKind.Object => options.EnumerateObject().ToDictionary(
                property => property.Name, property => (object?)property.Value.Clone(), StringComparer.Ordinal),
            JsonValueKind.Null or JsonValueKind.Undefined => new(StringComparer.Ordinal),
            _ => throw new ArgumentException("Xpectrum provider options must be a JSON object.")
        };

    private static JsonElement ToElement(object? value)
        => value is JsonElement json ? json : JsonSerializer.SerializeToElement(value);

    private static string? Str(JsonElement value, string name)
        => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.GetString())
                ? property.GetString() : null;

    private static string? StringValue(object? value) => value switch
    {
        string text when !string.IsNullOrWhiteSpace(text) => text,
        JsonElement { ValueKind: JsonValueKind.String } json when !string.IsNullOrWhiteSpace(json.GetString()) => json.GetString(),
        _ => null
    };

    private static async Task ThrowForStatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw CreateError(response.StatusCode, body, response.Headers.RetryAfter);
    }

    private static HttpRequestException CreateError(HttpStatusCode status, string body, RetryConditionHeaderValue? retryAfter = null)
    {
        var (code, message) = ReadError(body);
        var text = $"Xpectrum API error ({(int)status}{(code is null ? "" : $" {code}")}): {message}";
        var seconds = retryAfter?.Delta?.TotalSeconds
            ?? (retryAfter?.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : null);
        if (seconds is not null)
            text += $" Retry after {Math.Ceiling(seconds.Value).ToString(CultureInfo.InvariantCulture)}s.";
        var error = new HttpRequestException(text, null, status);
        if (code is not null)
            error.Data["code"] = code;
        if (seconds is not null)
            error.Data["retryAfterSeconds"] = Math.Ceiling(seconds.Value);
        return error;
    }

    private static (string? Code, string Message) ReadError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var error = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var nested)
                && nested.ValueKind == JsonValueKind.Object ? nested : root;
            var code = Str(error, "code") ?? Str(error, "type");
            var message = Str(error, "message") ?? Str(root, "detail") ?? Str(root, "error");
            if (message is not null)
                return (code, message);
        }
        catch (JsonException)
        {
        }
        var trimmed = body.Trim();
        return (null, trimmed.Length == 0 ? "No error details." : trimmed.Length > 500 ? trimmed[..500] : trimmed);
    }

    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Xpectrum has no native Responses API.");
    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException("Xpectrum has no native Responses API.");
    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(OpenAIDecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public IAsyncEnumerable<StreamingSpeechPart> SpeechStreamingAsync(StreamingSpeechRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
