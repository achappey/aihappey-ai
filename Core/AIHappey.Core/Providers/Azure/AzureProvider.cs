using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using AIHappey.Core.ModelProviders;

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

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        var modelId = chatRequest.GetModel();

        ArgumentNullException.ThrowIfNullOrEmpty(modelId);
        var model = await this.GetModel(modelId, cancellationToken: cancellationToken)
        ?? throw new ArgumentException(modelId);

        return model.Type switch
        {
            "speech" => await this.SpeechSamplingAsync(chatRequest, cancellationToken),
            "language" => await this.TranslateSamplingAsync(chatRequest, modelId!, cancellationToken),
            _ => throw new NotImplementedException(),
        };
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
         [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken)
            ?? throw new ArgumentException(chatRequest.Model);

        switch (model.Type)
        {
            case "transcription":
                {
                    await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }
            case "speech":

                {
                    await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }

            case "language":

                {
                    await foreach (var p in this.StreamTranslateAsync(chatRequest, cancellationToken))
                        yield return p;

                    yield break;
                }

            default:
                throw new NotImplementedException();
        }
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
     => throw new NotImplementedException();

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var modelId = options.Model ?? throw new ArgumentException(options.Model);
        var model = await this.GetModel(modelId, cancellationToken);

        if (model.Type == "speech")
            return await this.SpeechResponseAsync(options, cancellationToken);

        if (model.Type == "language")
            return await this.TranslateResponsesAsync(options, cancellationToken);

        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    Task<RealtimeResponse> IModelProvider.GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

