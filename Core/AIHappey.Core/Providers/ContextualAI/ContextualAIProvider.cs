using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.ContextualAI;

public partial class ContextualAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ContextualAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.contextual.ai/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(ContextualAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }



    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => "contextualai";

    public async Task<IEnumerable<Model>> ListModels(
     CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var owner = nameof(ContextualAI);

        return await Task.FromResult<IEnumerable<Model>>(
        [
        new Model
        {
            Id = "ctxl-rerank-v2-instruct-multilingual".ToModelId(GetIdentifier()),
            Name = "ctxl-rerank-v2-instruct-multilingual",
            OwnedBy = owner,
        },

        new Model
        {
            Id = "ctxl-rerank-v2-instruct-multilingual-mini".ToModelId(GetIdentifier()),
            Name = "ctxl-rerank-v2-instruct-multilingual-mini",
            OwnedBy = owner,
        },

        new Model
        {
            Id = "ctxl-rerank-v1-instruct".ToModelId(GetIdentifier()),
            Name = "ctxl-rerank-v1-instruct",
            OwnedBy = owner,
        },
    ]);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}