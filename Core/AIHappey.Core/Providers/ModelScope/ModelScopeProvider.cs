using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Core.Extensions;
using AIHappey.Core.Models;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Unified.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Vercel.Mapping;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.ModelScope;

public sealed partial class ModelScopeProvider(
    IApiKeyResolver keyResolver,
    IHttpClientFactory httpClientFactory,
    AsyncCacheHelper asyncCacheHelper) : IModelProvider
{
    private readonly HttpClient _client = CreateClient(httpClientFactory);
    private readonly HttpClient _mediaClient = httpClientFactory.CreateClient();

    private static HttpClient CreateClient(IHttpClientFactory factory)
    {
        var client = factory.CreateClient();
        client.BaseAddress = new Uri("https://api-inference.modelscope.ai/");
        return client;
    }

    public string GetIdentifier() => "modelscope";

    private string GetApiKey()
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No ModelScope API key.");

        return key;
    }

    private void ApplyAuthHeader()
        => _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", GetApiKey());

    public Task<ChatCompletion> CompleteChatAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletion(
            _client,
            options,
            relativeUrl: "v1/chat/completions",
            cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.GetChatCompletions(
            _client,
            options,
            relativeUrl: "v1/chat/completions",
            cancellationToken: cancellationToken);
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => ListModelsAsync(cancellationToken);

    private async Task<IEnumerable<Model>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var key = keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            return [];

        return await asyncCacheHelper.GetOrCreateAsync(
            this.GetCacheKey(key),
            async ct =>
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "v1/models");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await _client.SendAsync(request, ct);
                var content = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"ModelScope model discovery failed with status {(int)response.StatusCode}: {content}",
                        null,
                        response.StatusCode);

                using var document = JsonDocument.Parse(content);
                if (!document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array)
                    return [];

                return data.EnumerateArray()
                    .Select(ToModel)
                    .OfType<Model>()
                    .ToArray();
            },
            baseTtl: TimeSpan.FromHours(4),
            jitterMinutes: 480,
            cancellationToken: cancellationToken);
    }

    private Model? ToModel(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("id", out var idElement) ||
            idElement.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(idElement.GetString()))
            return null;

        var id = idElement.GetString()!;
        var name = item.TryGetProperty("name", out var nameElement) &&
                   nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        var modelType = ModelType(item);
        var model = new Model
        {
            Id = id.ToModelId(GetIdentifier()),
            Name = string.IsNullOrWhiteSpace(name) ? id : name,
            OwnedBy = item.TryGetProperty("owned_by", out var ownerElement) &&
                      ownerElement.ValueKind == JsonValueKind.String
                ? ownerElement.GetString() ?? GetIdentifier()
                : GetIdentifier(),
            Type = modelType,
            Created = item.TryGetProperty("created", out var createdElement) &&
                      createdElement.ValueKind == JsonValueKind.Number &&
                      createdElement.TryGetInt64(out var created)
                ? created
                : null
        };

        return model;
    }

    private static string ModelType(JsonElement item)
    {
        if (HasImageTask(item))
            return "image";

        return "language";
    }

    private static bool HasImageTask(JsonElement item)
    {
        foreach (var property in item.EnumerateObject())
        {
            if (property.NameEquals("type") || property.NameEquals("task") ||
                property.NameEquals("tasks") || property.NameEquals("capabilities") ||
                property.NameEquals("output_modalities"))
            {
                if (HasImageTaskValue(property.Value))
                    return true;
            }
        }

        return false;
    }

    private static bool HasImageTaskValue(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var name = value.GetString();
            return name is not null &&
                   (name.Equals("image", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("image_generation", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("image-generation", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("text-to-image", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("text_to_image", StringComparison.OrdinalIgnoreCase));
        }

        return value.ValueKind == JsonValueKind.Array &&
               value.EnumerateArray().Any(HasImageTaskValue);
    }

    public async Task<MessagesResponse> MessagesAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        CancellationToken cancellationToken = default)
    {
        var response = await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken);
        return response.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = request.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken)
                           .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = chatRequest.ToUnifiedRequest(GetIdentifier());
        await foreach (var part in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            foreach (var uiPart in part.Event.ToUIMessagePart(GetIdentifier()))
                yield return uiPart;
        }
    }

    public Task<AIResponse> ExecuteUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(
        AIRequest request,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);
    }

    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            .ToResponseResult();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(
        TranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(
        RerankingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RealtimeResponse> GetRealtimeToken(
        RealtimeRequest realtimeRequest,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<(byte[] Audio, string MimeType)> OpenAISpeechRequestAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IAudioSpeechStreamEvent> OpenAISpeechStreamingAsync(
        AudioSpeechRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(
        OpenAITranscriptionRequest options,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStartResult> StartVideoOperation(
        VideoRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(
        string operation,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(
        OpenAIEmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<EmbeddingResponse> EmbeddingRequestAsync(
        EmbeddingRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(
        StreamingTranscriptionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<OpenAIDecisionResponse> OpenAIDecisionRequestAsync(
        OpenAIDecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<DecisionResponse> DecisionRequestAsync(
        DecisionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
}
