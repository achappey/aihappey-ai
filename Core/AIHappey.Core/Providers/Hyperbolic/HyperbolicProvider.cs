using AIHappey.Core.AI;
using OAIC = OpenAI.Chat;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Core.Models;
using AIHappey.Common.Model.ChatCompletions;
using OpenAI.Responses;
using AIHappey.Common.Model;

namespace AIHappey.Core.Providers.Hyperbolic;

public partial class HyperbolicProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public HyperbolicProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.hyperbolic.xyz/");
    }


    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Hyperbolic)} API key.");

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

    public string GetIdentifier() => nameof(Hyperbolic).ToLowerInvariant();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return [.. HyperbolicModels, .. HyperbolicImageModels];
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

    public IReadOnlyList<Model> HyperbolicModels => new List<Model>
    {
        // ===== Qwen =====
        new()
        {
            Id = "Qwen/Qwen3-Next-80B-A3B-Thinking".ToModelId(GetIdentifier()),
            Name = "Qwen3-Next-80B-A3B-Thinking",
            Description = "Qwen3-Next thinker model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Next-80B-A3B-Instruct".ToModelId(GetIdentifier()),
            Name = "Qwen3-Next-80B-A3B-Instruct",
            Description = "Qwen3-Next instruct model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-Coder-480B-A35B-Instruct".ToModelId(GetIdentifier()),
            Name = "Qwen3-Coder-480B-A35B-Instruct",
            Description = "The latest and most powerful coder model from the Qwen Team.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-235B-A22B-Instruct-2507".ToModelId(GetIdentifier()),
            Name = "Qwen3-235B-A22B-Instruct-2507",
            Description = "Qwen latest non-thinking model with significant improvements in general capabilities.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen3-235B-A22B".ToModelId(GetIdentifier()),
            Name = "Qwen3-235B-A22B",
            Description = "A mixture-of-experts (MoE) model by Qwen with strong reasoning and agent tool-calling capabilities.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/QwQ-32B".ToModelId(GetIdentifier()),
            Name = "QwQ-32B",
            Description = "The latest Qwen reasoning model.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen2.5-Coder-32B".ToModelId(GetIdentifier()),
            Name = "Qwen2.5-Coder-32B",
            Description = "The best coder model from the Qwen Team.",
            Type = "language",
            OwnedBy = "Qwen"
        },
        new()
        {
            Id = "Qwen/Qwen2.5-72B".ToModelId(GetIdentifier()),
            Name = "Qwen2.5-72B",
            Description = "The latest Qwen LLM with more knowledge in coding and math.",
            Type = "language",
            OwnedBy = "Qwen"
        },

        // ===== OpenAI (OSS) =====
        new()
        {
            Id = "openai/gpt-oss-120b".ToModelId(GetIdentifier()),
            Name = "gpt-oss-120b",
            Description = "OpenAI’s open-weight model (big model smell).",
            Type = "language",
            OwnedBy = "OpenAI"
        },
        new()
        {
            Id = "openai/gpt-oss-20b".ToModelId(GetIdentifier()),
            Name = "gpt-oss-20b",
            Description = "OpenAI’s open-weight model (small model smell).",
            Type = "language",
            OwnedBy = "OpenAI"
        },

        // ===== Moonshot =====
        new()
        {
            Id = "moonshotai/Kimi-K2".ToModelId(GetIdentifier()),
            Name = "Kimi-K2",
            Description = "Kimi's latest 1T LLM, good at coding and tool-calling.",
            Type = "language",
            OwnedBy = "Moonshot"
        },

        // ===== DeepSeek =====
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1-0528".ToModelId(GetIdentifier()),
            Name = "DeepSeek-R1-0528",
            Description = "The latest open-source reasoner LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-R1".ToModelId(GetIdentifier()),
            Name = "DeepSeek-R1",
            Description = "The best open-source reasoner LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3-0324".ToModelId(GetIdentifier()),
            Name = "DeepSeek-V3-0324",
            Description = "DeepSeek's updated V3 model released on 03/24/2025.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },
        new()
        {
            Id = "deepseek-ai/DeepSeek-V3".ToModelId(GetIdentifier()),
            Name = "DeepSeek-V3",
            Description = "The best open-source LLM released by DeepSeek.",
            Type = "language",
            OwnedBy = "DeepSeek"
        },

        // ===== Meta =====
        new()
        {
            Id = "meta-llama/Llama-3.3-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3.3-70B",
            Description = "Meta's latest 70B LLM with performance comparable to Llama 3.1 405B.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.2-3B".ToModelId(GetIdentifier()),
            Name = "Llama-3.2-3B",
            Description = "The latest Llama 3.2 instruction-tuned model by Meta.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3-70B",
            Description = "A highly efficient and powerful model designed for a variety of tasks.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-405B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-405B",
            Description = "The biggest and best open-source AI model trained by Meta, beating GPT-4o across most benchmarks.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-70B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-70B",
            Description = "The best LLM at its size with faster response times compared to the 405B model.",
            Type = "language",
            OwnedBy = "Meta"
        },
        new()
        {
            Id = "meta-llama/Llama-3.1-8B".ToModelId(GetIdentifier()),
            Name = "Llama-3.1-8B",
            Description = "The smallest and fastest member of the Llama 3.1 family.",
            Type = "language",
            OwnedBy = "Meta"
        },
    };

    public IReadOnlyList<Model> HyperbolicImageModels =>
[
    // ===== Black Forest Labs =====
    new()
    {
        Id = "BlackForestLabs/Flux.1-dev".ToModelId(GetIdentifier()),
        Name = "BlackForestLabs/Flux.1-dev",
        Description = "A new SOTA image generation model that is outstanding in prompt following and visual quality.",
        Type = "image",
        OwnedBy = "BlackForestLabs"
    },

    // ===== Stability AI =====
    new()
    {
        Id = "StabilityAI/SDXL-1.0".ToModelId(GetIdentifier()),
        Name = "StabilityAI/SDXL-1.0",
        Description = "\"The High-Resolution Master\", excels at generating high-quality, detailed images with a focus on precision.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },
    new()
    {
        Id = "StabilityAI/Stable-Diffusion-1.5".ToModelId(GetIdentifier()),
        Name = "StabilityAI/Stable-Diffusion-1.5",
        Description = "\"The Reliable Classic\", known for its balanced performance and versatility.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },
    new()
    {
        Id = "StabilityAI/Stable-Diffusion-2".ToModelId(GetIdentifier()),
        Name = "StabilityAI/Stable-Diffusion-2",
        Description = "\"The Enhanced Innovator\", offers improved performance and capabilities over earlier versions.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },
    new()
    {
        Id = "StabilityAI/SDXL-1.0-Turbo".ToModelId(GetIdentifier()),
        Name = "StabilityAI/SDXL-1.0-Turbo",
        Description = "\"The Speedy Pro\", combines high-resolution outputs with faster processing times.",
        Type = "image",
        OwnedBy = "StabilityAI"
    },

    // ===== Segmind =====
    new()
    {
        Id = "Segmind/Segmind-SD-1B".ToModelId(GetIdentifier()),
        Name = "Segmind/Segmind-SD-1B",
        Description = "\"The Domain Specialist\", optimized for niche applications such as medical imaging and scientific visualization.",
        Type = "image",
        OwnedBy = "Segmind"
    },
];


}