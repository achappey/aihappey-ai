using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Zai;

public partial class ZaiProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ZaiProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.z.ai/api/paas/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Zai)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public float? GetPriority() => 1;

    public Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<OAIC.StreamingChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public string GetIdentifier() => nameof(Zai).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ZaiLanguageModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ResponseResult> CreateResponseAsync(ResponseReasoningOptions options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<ImageResponse> ImageRequest(ImageRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public static IReadOnlyList<Model> ZaiLanguageModels =>
[
    new()
    {
        Id = "zai/glm-4.7",
        Name = "glm-4.7",
        Description = "Latest flagship GLM-4.7 model, a foundational model specifically designed for agent applications.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.6",
        Name = "glm-4.6",
        Description = "Previous-generation GLM flagship model with strong general reasoning and agent capabilities.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.5",
        Name = "glm-4.5",
        Description = "GLM-4.5 base model offering balanced performance for general-purpose and agent workloads.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.5-air",
        Name = "glm-4.5-air",
        Description = "Lightweight GLM-4.5 variant optimized for faster inference and lower latency.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.5-x",
        Name = "glm-4.5-x",
        Description = "Enhanced GLM-4.5 variant with stronger reasoning and extended capability depth.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.5-airx",
        Name = "glm-4.5-airx",
        Description = "Hybrid GLM-4.5 model combining efficiency of AIR with enhanced reasoning performance.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4.5-flash",
        Name = "glm-4.5-flash",
        Description = "Ultra-fast GLM-4.5 variant optimized for low-latency, high-throughput workloads.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-4-32b-0414-128k",
        Name = "glm-4-32b-0414-128k",
        Description = "GLM-4 32B model with a 128k context window, suited for long-context reasoning and document-heavy tasks.",
        Type = "language",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/cogview-4-250304",
        Name = "cogview-4-250304",
        Type = "image",
        OwnedBy = "z.ai"
    },
    new()
    {
        Id = "zai/glm-asr-2512",
        Name = "glm-asr-2512",
        Type = "transcription",
        OwnedBy = "z.ai"
    },
];

}