using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using AIHappey.Core.ModelProviders;

namespace AIHappey.Core.Providers.ResembleAI;

public partial class ResembleAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ResembleAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://app.resemble.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(ResembleAI)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(ResembleAI).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ResembleAIModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = ResembleAIModels.FirstOrDefault(a => a.Id.EndsWith(chatRequest.Model))
            ?? throw new ArgumentException(chatRequest.Model);

        if (model.Type == "transcription")
        {
            await foreach (var p in this.StreamTranscriptionAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        if (model.Type == "speech")
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public async Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var modelId = options.Model ?? throw new ArgumentException(options.Model);
        var model = ResembleAIModels.FirstOrDefault(a => a.Id.EndsWith(modelId))
          ?? throw new ArgumentException(modelId);

        if (model.Type == "speech")
        {
            return await this.SpeechResponseAsync(options, cancellationToken);
        }

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

    public static IReadOnlyList<Model> ResembleAIModels =>
 [
    new() { Id = "speech-to-text".ToModelId(nameof(ResembleAI).ToLowerInvariant()),
        Name = "speech-to-text",
        Type = "transcription",
        OwnedBy = nameof(ResembleAI) },
    new() { Id = "chatterbox-turbo".ToModelId(nameof(ResembleAI).ToLowerInvariant()),
        Name = "Chatterbox-Turbo",
        Type = "speech",
        OwnedBy = nameof(ResembleAI) },
    new() { Id = "chatterbox".ToModelId(nameof(ResembleAI).ToLowerInvariant()),
        Name = "Chatterbox",
        Type = "speech",
        OwnedBy = nameof(ResembleAI) },
    new() { Id = "chatterbox-multilingual".ToModelId(nameof(ResembleAI).ToLowerInvariant()),
        Name = "Chatterbox-Multilingual",
        Type = "speech",
        OwnedBy = nameof(ResembleAI) },
];


}
