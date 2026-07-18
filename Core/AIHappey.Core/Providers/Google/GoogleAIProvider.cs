using AIHappey.Common.Model;
using Microsoft.Extensions.Logging;
using AIHappey.ChatCompletions.Models;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Responses.Mapping;
using AIHappey.Interactions.Mapping;
using AIHappey.Interactions;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Core.AI;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.Google;

public partial class GoogleAIProvider
    : IModelProvider
{
    private readonly AsyncCacheHelper _memoryCache;
    private readonly ILogger<GoogleAIProvider> _logger;
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public GoogleAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        ILogger<GoogleAIProvider> logger, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _logger = logger;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        _client.DefaultRequestHeaders.Remove("x-goog-api-key");
        _client.DefaultRequestHeaders.Add("x-goog-api-key", key);
    }

    private readonly string FILES_API = "https://generativelanguage.googleapis.com/v1beta/files";

    private Mscc.GenerativeAI.GoogleAI GetClient()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Google)} API key.");

        return new(key, logger: _logger);
    }

    private static readonly string Google = "Google";

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            var response = await this.ExecuteUnifiedAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken);

            return response.ToChatCompletion();
        }

        var result = await this.GetInteraction(options.ToUnifiedRequest(GetIdentifier()).ToInteractionRequest(GetIdentifier()),
            cancellationToken);

        return result.ToUnifiedResponse(GetIdentifier()).ToChatCompletion();
    }

    public string GetIdentifier() => GoogleExtensions.Identifier();

   

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = await this.GetModel(options.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var streamEvent in this.StreamUnifiedAsync(
                               options.ToUnifiedRequest(GetIdentifier()),
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return streamEvent.ToChatCompletionUpdate();
            }

            yield break;
        }

        var interactionRequest = options.ToUnifiedRequest(GetIdentifier()).ToInteractionRequest(GetIdentifier());
        interactionRequest.Stream = true;
        interactionRequest.Store = false;
        this.SetDefaultInteractionProperties(interactionRequest);

        await foreach (var update in GetInteractions(
                                 interactionRequest,
                                  cancellationToken: cancellationToken))
        {
            foreach (var item in update.ToUnifiedStreamEvent(GetIdentifier()))
            {
                yield return MarkGoogleAgentUnifiedToolEventProviderExecuted(item).ToChatCompletionUpdate();
            }
        }
    }

    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            var tResponse = await this.ExecuteUnifiedAsync(
                options.ToUnifiedRequest(GetIdentifier()),
                cancellationToken);

            return tResponse.ToResponseResult();
        }

        var interaction = await this.GetInteraction(options.ToUnifiedRequest(GetIdentifier()).ToInteractionRequest(GetIdentifier()),
            cancellationToken);

        var response = interaction.ToUnifiedResponse(GetIdentifier()).ToResponseResult();
        return EnrichResponseWithGatewayCost(response, options.Model, options.ServiceTier, interaction);
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = await this.GetModel(options.Model, cancellationToken);
        if (string.Equals(model.Type, "transcription", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var streamEvent in this.StreamUnifiedAsync(
                               options.ToUnifiedRequest(GetIdentifier()),
                               cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                yield return streamEvent.ToResponseStreamPart();
            }

            yield break;
        }

        var interactionRequest = options.ToUnifiedRequest(GetIdentifier()).ToInteractionRequest(GetIdentifier());
        interactionRequest.Stream = true;
        interactionRequest.Store = false;
        this.SetDefaultInteractionProperties(interactionRequest);

        Interaction? completedInteraction = null;

        await foreach (var update in GetInteractions(
                                  interactionRequest,
                                   cancellationToken: cancellationToken))
        {
            if (update is InteractionCompletedEvent { Interaction: not null } completedEvent)
                completedInteraction = completedEvent.Interaction;

            foreach (var item in update.ToUnifiedStreamEvent(GetIdentifier()))
            {
                var mappedItem = MarkGoogleAgentUnifiedToolEventProviderExecuted(item);
                Responses.Streaming.ResponseStreamPart part = mappedItem.ToResponseStreamPart();

                if (part is Responses.Streaming.ResponseCompleted completed)
                {
                    part = new Responses.Streaming.ResponseCompleted
                    {
                        SequenceNumber = completed.SequenceNumber,
                        Response = EnrichResponseWithGatewayCost(
                            completed.Response,
                            options.Model,
                            options.ServiceTier,
                            completedInteraction),
                        AdditionalProperties = completed.AdditionalProperties
                    };
                }

                yield return part;
            }
        }
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<MessagesResponse> MessagesAsync(MessagesRequest request, Dictionary<string, string> headers, CancellationToken cancellationToken = default)
    {
        var result = await this.ExecuteUnifiedAsync(request.ToUnifiedRequest(GetIdentifier()),
            cancellationToken);

        return result.ToMessagesResponse();
    }

    public async IAsyncEnumerable<MessageStreamPart> MessagesStreamingAsync(MessagesRequest request,
        Dictionary<string, string> headers,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var streamEvent in this.StreamUnifiedAsync(
                           request.ToUnifiedRequest(GetIdentifier()),
                           cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            foreach (var part in streamEvent.ToMessageStreamParts())
                yield return part;
        }
    }

  

    public Task<OpenAIImagesResponse> OpenAIImageGenerationRequestAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageGenerationStreamingAsync(OpenAIImageGenerationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageEditRequestAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<IOpenAIImageStreamEvent> OpenAIImageEditStreamingAsync(OpenAIImageEditRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<OpenAIImagesResponse> OpenAIImageVariationRequestAsync(OpenAIImageVariationRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

  
}
