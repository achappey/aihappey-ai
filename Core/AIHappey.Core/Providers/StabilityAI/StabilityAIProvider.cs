using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;

namespace AIHappey.Core.Providers.StabilityAI;

public partial class StabilityAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public StabilityAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.stability.ai/v2beta/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(StabilityAI)} API key.");

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

    public string GetIdentifier() => nameof(StabilityAI).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return
        [
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Name = "Stable Image Ultra",
                Type = "image",
                Id = "stable-image-ultra".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Image Core",
                Id = "stable-image-core".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Large",
                Id = "sd3.5-large".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Large Turbo",
                Id = "sd3.5-large-turbo".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Medium",
                Id = "sd3.5-medium".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "image",
                Name = "Stable Diffusion 3.5 Flash",
                Id = "sd3.5-flash".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "speech",
                Name = "Stable Audio 2.0",
                Id = "stable-audio-2".ToModelId(GetIdentifier())
            },
            new Model()
            {
                OwnedBy = nameof(StabilityAI),
                Type = "speech",
                Name = "Stable Audio 2.5",
                Id = "stable-audio-2.5".ToModelId(GetIdentifier())
            }
        ];
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (chatRequest.Model.Contains("audio") == true)
        {
            await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var update in this.StreamImageAsync(chatRequest, cancellationToken: cancellationToken))
            yield return update;
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}
