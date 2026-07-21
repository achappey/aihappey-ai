using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;
using System.Globalization;
using System.Text.Json;

namespace AIHappey.Core.Providers.Neuralwatt;

public partial class NeuralwattProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public NeuralwattProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.neuralwatt.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Neuralwatt)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ChatCompletionUpdate? pendingUsageUpdate = null;
        string? lastFinishReason = null;

        await foreach (var streamItem in _client.GetChatCompletionSseEvents(
            options,
            GetIdentifier(),
            capture: options.GetNeuralwattBackendCapture(GetIdentifier()),
            headers: NeuralwattExtensions.MergeRequestHeaders(
                this.SetDefaultChatCompletionProperties(options),
                options.Headers),
            ct: cancellationToken))
        {
            if (streamItem.Comment is { } comment
                && TryGetRequestCostUsd(comment, out var requestCostUsd))
            {
                if (pendingUsageUpdate is not null)
                {
                    yield return EnrichStreamingUpdateWithGatewayCost(
                        pendingUsageUpdate,
                        requestCostUsd,
                        lastFinishReason);
                    pendingUsageUpdate = null;
                }

                continue;
            }

            if (streamItem.Data is not { Length: > 0 } data)
                continue;

            ChatCompletionUpdate? update;
            try
            {
                update = JsonSerializer.Deserialize<ChatCompletionUpdate>(data, JsonSerializerOptions.Web);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse Neuralwatt SSE JSON event: {data}", ex);
            }

            if (update is null)
                continue;

            if (!string.IsNullOrWhiteSpace(update.Model))
                update.Model = $"{GetIdentifier()}/{update.Model}";

            if (update.Created == 0)
                update.Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            var finishReason = TryGetFinishReason(update);
            if (!string.IsNullOrWhiteSpace(finishReason))
                lastFinishReason = finishReason;

            if (pendingUsageUpdate is not null)
            {
                yield return NormalizeStreamingUpdateForGatewayCost(
                    pendingUsageUpdate,
                    lastFinishReason);
                pendingUsageUpdate = null;
            }

            if (update.Usage is not null)
            {
                pendingUsageUpdate = update;
                continue;
            }

            yield return update;
        }

        if (pendingUsageUpdate is not null)
        {
            yield return NormalizeStreamingUpdateForGatewayCost(
                pendingUsageUpdate,
                lastFinishReason);
        }
    }

    public string GetIdentifier() => nameof(Neuralwatt).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }

        yield break;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
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

        yield break;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
      => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(AudioSpeechRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    private static bool TryGetRequestCostUsd(string payload, out decimal requestCostUsd)
    {
        requestCostUsd = 0m;

        if (!payload.StartsWith("cost", StringComparison.OrdinalIgnoreCase))
            return false;

        try
        {
            using var document = JsonDocument.Parse(payload["cost".Length..].Trim());
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("request_cost_usd", out var cost)
                   && TryGetDecimal(cost, out requestCostUsd);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryGetDecimal(JsonElement value, out decimal result)
    {
        result = 0m;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out result),
            JsonValueKind.String => decimal.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out result),
            _ => false
        };
    }

    private static ChatCompletionUpdate EnrichStreamingUpdateWithGatewayCost(
        ChatCompletionUpdate update,
        decimal cost,
        string? lastFinishReason)
    {
        NormalizeStreamingUpdateForGatewayCost(update, lastFinishReason);

        var properties = update.AdditionalProperties is not null
            ? new Dictionary<string, JsonElement>(update.AdditionalProperties, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var metadata = properties.TryGetValue("metadata", out var metadataElement)
                       && metadataElement.ValueKind == JsonValueKind.Object
            ? JsonSerializer.Deserialize<Dictionary<string, object?>>(metadataElement.GetRawText(), JsonSerializerOptions.Web) ?? []
            : [];

        metadata["gateway"] = new Dictionary<string, object?>
        {
            ["cost"] = cost
        };
        properties["metadata"] = JsonSerializer.SerializeToElement(metadata, JsonSerializerOptions.Web);
        update.AdditionalProperties = properties;

        return update;
    }

    private static ChatCompletionUpdate NormalizeStreamingUpdateForGatewayCost(
        ChatCompletionUpdate update,
        string? lastFinishReason)
    {
        if (update.Usage is null
            || update.Choices.Any()
            || string.IsNullOrWhiteSpace(lastFinishReason))
        {
            return update;
        }

        update.Choices =
        [
            new
            {
                index = 0,
                delta = new { },
                finish_reason = lastFinishReason
            }
        ];

        return update;
    }

    private static string? TryGetFinishReason(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices)
        {
            var choiceElement = JsonSerializer.SerializeToElement(choice, JsonSerializerOptions.Web);
            if (choiceElement.TryGetProperty("finish_reason", out var finishReason)
                && finishReason.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(finishReason.GetString()))
            {
                return finishReason.GetString();
            }
        }

        return null;
    }

}
