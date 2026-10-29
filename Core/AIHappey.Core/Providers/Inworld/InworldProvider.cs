using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIHappey.Core.Models;
using AIHappey.Common.Model.Providers.OpenAI.Live;

namespace AIHappey.Core.Providers.Inworld;

public partial class InworldProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly IEndUserIdResolver _endUserIdResolver;
    private readonly AsyncCacheHelper _memoryCache;

    private readonly HttpClient _client;

    public InworldProvider(
        IApiKeyResolver keyResolver,
        AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory,
        IEndUserIdResolver endUserIdResolver)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.inworld.ai/");
        _endUserIdResolver = endUserIdResolver;
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Inworld)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        PrepareChatCompletionOptions(options);

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        PrepareChatCompletionOptions(options);

        return this.GetChatCompletions(_client,
                    options, cancellationToken: cancellationToken);
    }

    private static void PrepareChatCompletionOptions(ChatCompletionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = options.Model?.Trim();
        if (string.IsNullOrWhiteSpace(model))
            return;

        const string providerPrefix = "inworld/";
        if (!model.StartsWith(providerPrefix, StringComparison.OrdinalIgnoreCase))
            return;

        var rawModel = model[providerPrefix.Length..];
        if (string.Equals(rawModel, "auto", StringComparison.OrdinalIgnoreCase))
        {
            options.Model = "auto";
            return;
        }

        var separatorIndex = rawModel.IndexOf('/');
        if (separatorIndex <= 0)
            return;

        var provider = rawModel[..separatorIndex];
        if (!provider.StartsWith("SERVICE_PROVIDER_", StringComparison.OrdinalIgnoreCase))
            return;

        var providerModel = rawModel[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(providerModel))
            return;

        options.Model = providerModel;
        SetExtraBodyProvider(options, provider.ToUpperInvariant());
    }

    private static void SetExtraBodyProvider(ChatCompletionOptions options, string provider)
    {
        options.AdditionalProperties ??= new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

        var extraBody = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        if (options.AdditionalProperties.TryGetValue("extra_body", out var extraBodyElement)
            && extraBodyElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in extraBodyElement.EnumerateObject())
                extraBody[property.Name] = property.Value.Clone();
        }

        extraBody["provider"] = JsonSerializer.SerializeToElement(provider, JsonSerializerOptions.Web);
        options.AdditionalProperties["extra_body"] = JsonSerializer.SerializeToElement(extraBody, JsonSerializerOptions.Web);
    }

    public string GetIdentifier() => nameof(Inworld).ToLowerInvariant();

    

  

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        NormalizeInworldResponsesModel(options);
        ApplyAuthHeader();
        return await this.GetResponse(_client, options,
            relativeUrl: "v1/responses",
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        NormalizeInworldResponsesModel(options);
        ApplyAuthHeader();
        await foreach (var part in this.GetResponses(_client, options,
                           relativeUrl: "v1/responses",
                           cancellationToken: cancellationToken))
            yield return part;
    }

    private static void NormalizeInworldResponsesModel(Responses.ResponseRequest options)
    {
        ArgumentNullException.ThrowIfNull(options);
        const string prefix = "inworld/";
        if (options.Model?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true)
            options.Model = options.Model[prefix.Length..];
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
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
            cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;

        yield break;
    }


    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
     => this.ExecuteUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => this.StreamUnifiedViaChatCompletionsAsync(request, cancellationToken: cancellationToken);

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

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<OpenAIEmbeddingResponse> OpenAIEmbeddingRequestAsync(OpenAIEmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<EmbeddingResponse> EmbeddingRequestAsync(EmbeddingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<StreamingTranscriptionPart> TranscriptionStreamingAsync(StreamingTranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Inworld live STT requires a client-owned WebSocket connection. Backend WebSocket transcription transport is not supported.");
    }

    public Task<DecisionResponse> DecisionRequestAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        => ExecuteInworldDecisionAsync(request, cancellationToken);

    public Task<LiveSessionResponse> CreateLiveSessionAsync(LiveSessionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Inworld supports generic Realtime WebRTC setup, not the OpenAI Live session contract.");
    }
}
