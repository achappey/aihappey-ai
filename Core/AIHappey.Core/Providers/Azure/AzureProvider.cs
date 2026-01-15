using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;

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

    private Uri GetHostUri()
    {
        if (string.IsNullOrWhiteSpace(_endpoint))
            throw new InvalidOperationException("No Azure Speech endpoint configured.");

        var endpoint = _endpoint.Trim();
        if (endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return new Uri(endpoint, UriKind.Absolute);

        return new Uri("https://" + endpoint.TrimStart('/').TrimEnd('/'), UriKind.Absolute);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
            yield return p;
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

