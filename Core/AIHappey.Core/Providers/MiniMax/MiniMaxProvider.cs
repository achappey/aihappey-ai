using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

// speech
using System.Net.Mime;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIHappey.Common.Extensions;
using AIHappey.Common.Model.Providers.MiniMax;

namespace AIHappey.Core.Providers.MiniMax;

public partial class MiniMaxProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public MiniMaxProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.minimax.io/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(MiniMax)} API key.");

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

    public string GetIdentifier() => nameof(MiniMax).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return MiniMaxModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public static IReadOnlyList<Model> MiniMaxModels =>
        [
            // ===== MiniMax =====
            new() { Id = "minimax/MiniMax-M2.1",
                Name = "MiniMax M2.1",
                Type = "language",
                OwnedBy = nameof(MiniMax) },
            new() { Id = "minimax/MiniMax-M2.1-lightning",
                Name = "MiniMax M2.1 Lightning",
                Type = "language",
                OwnedBy = nameof(MiniMax) },
            new() { Id = "minimax/MiniMax-M2",
                Name = "MiniMax M2",
                Type = "language",
                OwnedBy = nameof(MiniMax) },

            // ===== MiniMax Images =====
            // Expose MiniMax image model as "minimax/image-01" so it routes consistently through the resolver.
            new()
            {
                Id = "image-01".ToModelId(nameof(MiniMax).ToLowerInvariant()),
                Name = "MiniMax Image",
                Type = "image",
                OwnedBy = nameof(MiniMax)
            },

            // ===== MiniMax Speech (Text-to-Audio) =====
            new() { Id = "speech-2.6-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-2.6-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-2.6-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-2.6-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-02-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-02-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-02-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-02-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-01-hd".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-01-hd", Type = "speech", OwnedBy = nameof(MiniMax) },
            new() { Id = "speech-01-turbo".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "speech-01-turbo", Type = "speech", OwnedBy = nameof(MiniMax) },

            // ===== MiniMax Speech (Music) =====
            new() { Id = "music-2.0".ToModelId(nameof(MiniMax).ToLowerInvariant()), Name = "music-2.0", Type = "speech", OwnedBy = nameof(MiniMax) },
        ];

}
