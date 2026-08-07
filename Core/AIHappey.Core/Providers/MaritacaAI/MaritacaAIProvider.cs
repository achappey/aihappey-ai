using AIHappey.Core.AI;
using System.Net.Http.Headers;
using AIHappey.ChatCompletions.Models;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Messages;
using AIHappey.Responses.Extensions;
using AIHappey.Responses;
using AIHappey.Responses.Mapping;
using AIHappey.Messages.Mapping;
using AIHappey.Unified.Models;
using System.Runtime.CompilerServices;
using AIHappey.Core.Models;

namespace AIHappey.Core.Providers.MaritacaAI;

public partial class MaritacaAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    private readonly AsyncCacheHelper _memoryCache;

    public MaritacaAIProvider(IApiKeyResolver keyResolver, AsyncCacheHelper asyncCacheHelper,
        IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _memoryCache = asyncCacheHelper;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://chat.maritaca.ai/api/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(MaritacaAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await this.GetChatCompletion(_client,
             options, cancellationToken: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return this.GetChatCompletions(_client,
                    options, cancellationToken: cancellationToken);
    }

    public string GetIdentifier() => nameof(MaritacaAI).ToLowerInvariant();

    

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var headers = this.SetDefaultResponseProperties(options);
        NormalizeMaritacaIntegratedTools(options);

        var response = await _client.GetResponses(
            options,
            GetIdentifier(),
            headers: headers,
            ct: cancellationToken);

        return response;
    }

    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        var headers = this.SetDefaultResponseProperties(options);
        NormalizeMaritacaIntegratedTools(options);

        await foreach (var update in _client.GetResponsesUpdates(
           options,
           providerId: GetIdentifier(),
           headers: headers,
           ct: cancellationToken))
        {
            yield return update;
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

        yield break;
    }

    public Task<AIResponse> ExecuteUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
        => ExecuteMaritacaUnifiedAsync(request, cancellationToken);

    public IAsyncEnumerable<AIStreamEvent> StreamUnifiedAsync(AIRequest request, CancellationToken cancellationToken = default)
       => StreamMaritacaUnifiedAsync(request, cancellationToken);

    private async Task<AIResponse> ExecuteMaritacaUnifiedAsync(AIRequest request, CancellationToken cancellationToken)
    {
        var responseRequest = request.ToResponseRequest(GetIdentifier());
        responseRequest.Stream = false;
        responseRequest.Store ??= false;
        NormalizeResponseInput(responseRequest);

        var response = await ResponsesAsync(responseRequest, cancellationToken);
        return response.ToUnifiedResponse(GetIdentifier());
    }

    private async IAsyncEnumerable<AIStreamEvent> StreamMaritacaUnifiedAsync(
        AIRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var responseRequest = request.ToResponseRequest(GetIdentifier());
        responseRequest.Stream = true;
        responseRequest.Store ??= false;
        NormalizeResponseInput(responseRequest);

        await foreach (var update in ResponsesStreamingAsync(responseRequest, cancellationToken))
        {
            foreach (var streamEvent in update.ToUnifiedStreamEvent(GetIdentifier()))
                yield return streamEvent;
        }
    }

    private static void NormalizeResponseInput(ResponseRequest request)
    {
        if (request.Input?.Items is null)
            return;

        var normalizedItems = request.Input.Items.Select(item =>
        {
            if (item is not ResponseInputMessage message
                || message.Role != ResponseRole.Assistant
                || !message.Content.IsParts
                || message.Content.Parts?.All(part => part is OutputTextPart) != true)
            {
                return item;
            }

            return new ResponseInputMessage
            {
                Id = message.Id,
                Role = message.Role,
                Status = message.Status,
                Phase = message.Phase,
                Content = new ResponseMessageContent(string.Concat(
                    message.Content.Parts.Cast<OutputTextPart>().Select(part => part.Text)))
            };
        });

        request.Input = new ResponseInput(normalizedItems);
    }

    private static void NormalizeMaritacaIntegratedTools(ResponseRequest request)
    {
        foreach (var tool in request.Tools ?? [])
        {
            var name = tool.Extra is not null && tool.Extra.TryGetValue("name", out var nameElement)
                ? nameElement.GetString()
                : null;

            if (string.Equals(name, "web_search", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "code_interpreter", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "data_ocean", StringComparison.OrdinalIgnoreCase))
            {
                tool.Type = name;
                tool.Extra = null;
            }
            else if (string.Equals(name, "code_execution", StringComparison.OrdinalIgnoreCase))
            {
                tool.Type = "code_interpreter";
                tool.Extra = null;
            }
        }
    }

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

    

    public Task<IOpenAITranscriptionResponse> OpenAITranscriptionRequestAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public IAsyncEnumerable<IOpenAITranscriptionStreamEvent> OpenAITranscriptionStreamingAsync(OpenAITranscriptionRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }

    public Task<VideoOperationStartResult> StartVideoOperation(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<VideoOperationStatusResult> GetVideoOperationStatus(string operation, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
