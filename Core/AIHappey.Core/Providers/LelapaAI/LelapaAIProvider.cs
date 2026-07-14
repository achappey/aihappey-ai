using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Messages.Mapping;
using AIHappey.ChatCompletions.Mapping;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Responses.Mapping;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using System.Runtime.CompilerServices;
using AIHappey.Common.Extensions;

namespace AIHappey.Core.Providers.LelapaAI;

public partial class LelapaAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;


    public LelapaAIProvider(IApiKeyResolver keyResolver,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.lelapa.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(LelapaAI)} API key.");

        _client.DefaultRequestHeaders.Remove("X-CLIENT-TOKEN");
        _client.DefaultRequestHeaders.Add("X-CLIENT-TOKEN", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        var result = await ExecuteUnifiedAsync(options.ToUnifiedRequest(GetIdentifier()),
             cancellationToken);

        return result.ToChatCompletion();
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var unifiedRequest = options.ToUnifiedRequest(GetIdentifier());

        await foreach (var part in this.StreamUnifiedAsync(
            unifiedRequest,
            cancellationToken))
        {
            yield return part.ToChatCompletionUpdate();
        }
    }

    public string GetIdentifier() => nameof(LelapaAI).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel()
            ?? throw new ArgumentException("Model is required.", nameof(chatRequest));

        var metadata = chatRequest.Metadata is null
            ? null
            : chatRequest.Metadata.ToObjectDictionary();

        var unifiedRequest = new AIHappey.Unified.Models.AIRequest
        {
            ProviderId = GetIdentifier(),
            Model = modelId,
            Metadata = metadata,
            Input = new AIHappey.Unified.Models.AIInput
            {
                Items =
                [
                    .. chatRequest.Messages
                        .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
                        .Select(m => new AIHappey.Unified.Models.AIInputItem
                        {
                            Role = "user",
                            Content =
                            [
                                .. m.Content
                                    .OfType<TextContentBlock>()
                                    .Where(block => !string.IsNullOrWhiteSpace(block.Text))
                                    .Select(block => new AIHappey.Unified.Models.AITextContentPart
                                    {
                                        Text = block.Text,
                                        Type = "text",
                                    })
                            ]
                        })
                ]
            }
        };

        var response = await ExecuteUnifiedAsync(unifiedRequest, cancellationToken);
        var text = response.Output?.Items?
            .SelectMany(item => item.Content ?? [])
            .OfType<AIHappey.Unified.Models.AITextContentPart>()
            .Select(part => part.Text)
            .FirstOrDefault() ?? string.Empty;

        return new CreateMessageResult
        {
            Role = ModelContextProtocol.Protocol.Role.Assistant,
            Model = modelId,
            StopReason = "stop",
            Content = [text.ToTextContentBlock()]
        };
    }


    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<Responses.ResponseResult> ResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        return (await ExecuteUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
            .ToResponseResult();
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var part in StreamUnifiedAsync(
            options.ToUnifiedRequest(GetIdentifier()),
            cancellationToken))
        {
            yield return part.ToResponseStreamPart();
        }
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
    }

}
