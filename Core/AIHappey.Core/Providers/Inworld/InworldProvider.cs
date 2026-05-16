using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Sampling.Mapping;
using AIHappey.Core.AI;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using System.Text.Json;

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

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(chatRequest.ToUnifiedRequest(GetIdentifier()),
           cancellationToken);

        return result.ToSamplingResult();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
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
}
