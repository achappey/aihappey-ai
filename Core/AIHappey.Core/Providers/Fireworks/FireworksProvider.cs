using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Common.Model.Responses;

namespace AIHappey.Core.Providers.Fireworks;

public partial class FireworksProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public FireworksProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.fireworks.ai/inference/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Fireworks)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        options.ToolChoice ??= "auto";

        return await _client.GetChatCompletion(
           options, ct: cancellationToken);
    }

    public string GetIdentifier() => nameof(Fireworks).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return FireworksModels;
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        ApplyAuthHeader();
        options.ToolChoice ??= "auto";

        return _client.GetChatCompletionUpdates(
            options, ct: cancellationToken);
    }

     public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetResponses(
                   options, ct: cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetResponsesUpdates(
           options,
           ct: cancellationToken);
    }

    public static IReadOnlyList<Model> FireworksModels =>
        [
            // ===== MiniMax =====
            new() { Id = "fireworks/accounts/fireworks/models/minimax-m2p1", Name = "MiniMax M2.1", Type = "language", OwnedBy = "minimax" },
            new() { Id = "fireworks/accounts/fireworks/models/minimax-m2",   Name = "MiniMax M2",   Type = "language", OwnedBy = "minimax" },

            // ===== Z.ai (GLM) =====
            new() { Id = "fireworks/accounts/fireworks/models/glm-4.7", Name = "GLM-4.7", Type = "language", OwnedBy = "z.ai" },
            new() { Id = "fireworks/accounts/fireworks/models/glm-4.6", Name = "GLM-4.6", Type = "language", OwnedBy = "z.ai" },
            new() { Id = "fireworks/accounts/fireworks/models/glm-4.5", Name = "GLM-4.5", Type = "language", OwnedBy = "z.ai" },

            // ===== DeepSeek =====
            new() { Id = "fireworks/accounts/fireworks/models/deepseek-v3.2",        Name = "DeepSeek V3.2",        Type = "language", OwnedBy = "deepseek" },
            new() { Id = "fireworks/accounts/fireworks/models/deepseek-v3.1",        Name = "DeepSeek V3.1",        Type = "language", OwnedBy = "deepseek" },
            new() { Id = "fireworks/accounts/fireworks/models/deepseek-v3.1-terminus",Name = "DeepSeek V3.1 Terminus",Type = "language", OwnedBy = "deepseek" },
            new() { Id = "fireworks/accounts/fireworks/models/deepseek-v3-03-24",    Name = "DeepSeek V3 (03-24)",  Type = "language", OwnedBy = "deepseek" },
            new() { Id = "fireworks/accounts/fireworks/models/deepseek-r1-05-28",    Name = "DeepSeek R1 (05-28)",  Type = "language", OwnedBy = "deepseek" },

            // ===== Moonshot =====
            new() { Id = "fireworks/accounts/fireworks/models/kimi-k2-thinking",      Name = "Kimi K2 Thinking",        Type = "language", OwnedBy = "moonshot" },
            new() { Id = "fireworks/accounts/fireworks/models/kimi-k2-instruct-0905", Name = "Kimi K2 Instruct 0905",   Type = "language", OwnedBy = "moonshot" },

            // ===== Qwen =====
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-235b-a22b",                Name = "Qwen3 235B A22B",                Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-235b-a22b-thinking-2507",   Name = "Qwen3 235B Thinking 2507",       Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-235b-a22b-instruct-2507",   Name = "Qwen3 235B Instruct 2507",       Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-coder-480b-a35b-instruct",  Name = "Qwen3 Coder 480B",               Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-8b",                        Name = "Qwen3 8B",                      Type = "language", OwnedBy = "qwen" },

            // ===== Qwen Vision =====
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-vl-235b-a22b-thinking", Name = "Qwen3 VL 235B Thinking", Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-vl-235b-a22b-instruct", Name = "Qwen3 VL 235B Instruct", Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-vl-30b-a3b-thinking",   Name = "Qwen3 VL 30B Thinking",  Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen3-vl-30b-a3b-instruct",   Name = "Qwen3 VL 30B Instruct",  Type = "language", OwnedBy = "qwen" },
            new() { Id = "fireworks/accounts/fireworks/models/qwen2.5-vl-32b-instruct",     Name = "Qwen2.5 VL 32B",         Type = "language", OwnedBy = "qwen" },

            // ===== Meta =====
            new() { Id = "fireworks/accounts/fireworks/models/llama-3.3-70b-instruct", Name = "Llama 3.3 70B Instruct", Type = "language", OwnedBy = "meta" },
            new() { Id = "fireworks/accounts/fireworks/models/llama4-maverick-instruct-basic", Name = "Llama 4 Maverick Instruct", Type = "language", OwnedBy = "meta" },

            // ===== OpenAI OSS =====
            new() { Id = "fireworks/accounts/fireworks/models/gpt-oss-120b", Name = "GPT-OSS 120B", Type = "language", OwnedBy = "openai" },
            new() { Id = "fireworks/accounts/fireworks/models/gpt-oss-20b",  Name = "GPT-OSS 20B",  Type = "language", OwnedBy = "openai" },

            new() { Id = "fireworks/whisper-v3", Name = "Whisper v3", Type = "transcription", OwnedBy = "openai" },
            new() { Id = "fireworks/whisper-v3-turbo",  Name = "Whisper v3 Turbo",  Type = "transcription", OwnedBy = "openai" },

            // ===== Qwen3 Rerankers =====
            new() { Id = "fireworks/fireworks/qwen3-reranker-8b",   Name = "Qwen3 Reranker 8B",   Type = "reranking", OwnedBy = "qwen" },

            // ===== Image (workflows) =====
            new() { Id = "fireworks/accounts/fireworks/models/flux-kontext-pro", Name = "Flux Kontext Pro", Type = "image", OwnedBy = "fireworks" },
            new() { Id = "fireworks/accounts/fireworks/models/flux-kontext-max", Name = "Flux Kontext Max", Type = "image", OwnedBy = "fireworks" },
            new() { Id = "fireworks/accounts/fireworks/models/flux-1-schnell-fp8", Name = "Flux 1 Schnell (FP8)", Type = "image", OwnedBy = "fireworks" },
            new() { Id = "fireworks/accounts/fireworks/models/flux-1-dev-fp8", Name = "Flux 1 Dev (FP8)", Type = "image", OwnedBy = "fireworks" },

            // ===== Image (image_generation) =====
            new() { Id = "fireworks/accounts/fireworks/models/stable-diffusion-xl-1024-v1-0", Name = "Stable Diffusion XL 1024", Type = "image", OwnedBy = "stabilityai" },
            new() { Id = "fireworks/accounts/fireworks/models/playground-v2-1024px-aesthetic", Name = "Playground v2 1024px Aesthetic", Type = "image", OwnedBy = "playground" },
            new() { Id = "fireworks/accounts/fireworks/models/playground-v2-5-1024px-aesthetic", Name = "Playground v2.5 1024px Aesthetic", Type = "image", OwnedBy = "playground" },
            new() { Id = "fireworks/accounts/fireworks/models/SSD-1B", Name = "SSD-1B", Type = "image", OwnedBy = "fireworks" },
            new() { Id = "fireworks/accounts/fireworks/models/japanese-stable-diffusion-xl", Name = "Japanese Stable Diffusion XL", Type = "image", OwnedBy = "stabilityai" },


        ];

}
