using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Sarvam;

/// <summary>
/// Sarvam Chat Completions API.
/// Base URL: https://api.sarvam.ai/
/// Endpoint: POST /v1/chat/completions
/// Auth header: api-subscription-key: &lt;apiKey&gt;
/// </summary>
public sealed partial class SarvamProvider : IModelProvider
{

    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public SarvamProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.sarvam.ai/");
    }


    private const string ProviderId = "sarvam";

    public string GetIdentifier() => ProviderId;

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Sarvam API key.");

        // Sarvam uses a custom header auth.
        _client.DefaultRequestHeaders.Remove("api-subscription-key");
        _client.DefaultRequestHeaders.Add("api-subscription-key", key);
    }

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => Task.FromResult<IEnumerable<Model>>(
        [
            new Model
            {
                Id = "sarvam-m".ToModelId(GetIdentifier()),
                Name = "sarvam-m",
                OwnedBy = nameof(Sarvam),
                Type = "language"
            },
             new Model
            {
                Id = "saarika:v2.5".ToModelId(GetIdentifier()),
                Name = "saarika:v2.5",
                OwnedBy = nameof(Sarvam),
                Type = "transcription"
            },
             new Model
            {
                Id = "bulbul:v2".ToModelId(GetIdentifier()),
                Name = "bulbul:v2",
                OwnedBy = nameof(Sarvam),
                Type = "speech"
            }
        ]);

    // ---------------------------------------------------------------------
    // Chat Completions (OpenAI-ish surface)
    // ---------------------------------------------------------------------

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException("Sarvam streaming is not implemented (one-shot only).");


    // ---------------------------------------------------------------------
    // Not implemented surfaces
    // ---------------------------------------------------------------------

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    // Implemented in SarvamProvider.Transcriptions.cs

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

