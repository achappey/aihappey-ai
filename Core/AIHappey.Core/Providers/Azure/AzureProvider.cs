using AIHappey.Common.Model;
using AIHappey.ChatCompletions.Models;
using AIHappey.Core.AI;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Messages.Mapping;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Vercel.Models;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Options;
using AIHappey.Core.Models;
using System.Runtime.CompilerServices;
using AIHappey.Responses.Mapping;

namespace AIHappey.Core.Providers.Azure;

/// <summary>
/// Azure Cognitive Services Speech provider.
/// Supports Speech-to-Text via Azure Speech SDK.
/// </summary>
public sealed partial class AzureProvider(
    IApiKeyResolver keyResolver,
    IOptions<AzureProviderOptions> options,
    AsyncCacheHelper memoryCache,
    IHttpClientFactory httpClientFactory) : IModelProvider, ISkillProvider, IConfiguredSkillProvider
{
    private readonly IApiKeyResolver _keyResolver = keyResolver;
    private readonly AsyncCacheHelper _memoryCache = memoryCache;
    private readonly string? _endpoint = options.Value.Endpoint;
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient();
    private readonly BlobContainerClient? _skillsContainerClient = CreateSkillsContainerClient(options.Value.SkillsStorage);

    public string GetIdentifier() => "azure";

    public bool HasConfiguredSkillSource => IsSkillsStorageEnabled();

    private string GetKey()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Azure Speech API key.");
        return key;
    }

    private string GetEndpointRegion()
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            throw new InvalidOperationException("No Azure Speech endpoint configured.");

        var endpoint = _endpoint.Trim();
        var separator = endpoint.IndexOf(':');
        return separator < 0 ? endpoint : endpoint[..separator].Trim();
    }

    private string? GetDocumentIntelligenceEndpoint()
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            return null;

        var separator = _endpoint.IndexOf(':');
        if (separator < 0 || separator == _endpoint.Length - 1)
            return null;

        var resource = _endpoint[(separator + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(resource))
            return null;

        return $"https://{resource}.cognitiveservices.azure.com";
    }



    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToChatCompletion();

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var item in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken))
            yield return item.ToChatCompletionUpdate();
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
        => (await ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)).ToMessagesResponse();

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(
        MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()), cancellationToken)
            .ToMessageStreamParts(request.Model, cancellationToken))
            yield return part;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()), cancellationToken)
                           .ToResponseStreamParts(cancellationToken))
            yield return part;
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
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
        throw new NotImplementedException();
    }
}

