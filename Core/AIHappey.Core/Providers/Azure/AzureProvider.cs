using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using Microsoft.Extensions.Options;
using AIHappey.Core.ModelProviders;
using AIHappey.Vercel.Models;

namespace AIHappey.Core.Providers.Azure;

/// <summary>
/// Azure Cognitive Services Speech provider.
/// Supports Speech-to-Text via Azure Speech SDK.
/// </summary>
public sealed partial class AzureProvider(
    IApiKeyResolver keyResolver,
    IOptions<AzureProviderOptions> options) : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver = keyResolver;
    private readonly string? _endpoint = options.Value.Endpoint;

    public string GetIdentifier() => "azure";

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

        return endpoint;
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();


    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

