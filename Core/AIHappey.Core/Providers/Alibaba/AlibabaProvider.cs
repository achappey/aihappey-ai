using System.Net.Http.Headers;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;
using OAIC = OpenAI.Chat;
using OpenAI.Responses;

namespace AIHappey.Core.Providers.Alibaba;

public partial class AlibabaProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public AlibabaProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://dashscope-intl.aliyuncs.com/compatible-mode/v1/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No Alibaba (DashScope) API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public string GetIdentifier() => "alibaba";

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await _client.GetChatCompletion(
             options,
             relativeUrl: "chat/completions",
             ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return _client.GetChatCompletionUpdates(
                    options,
                    relativeUrl: "chat/completions",
                    ct: cancellationToken);
    }

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        // Alibaba DashScope does not expose a public list-models endpoint for compatible-mode.
        // We hardcode common Qwen "flagship" model names.
        ApplyAuthHeader();

        return Task.FromResult<IEnumerable<Model>>(
        [
            new()
            {
                Id = "qwen-max".ToModelId(GetIdentifier()),
                Name = "qwen-max",
                Type = "language",
                OwnedBy = "Alibaba",
                ContextWindow = 262144,
              //  Pricing = new ModelPricing { Input = "1.2", Output = "6" }
            },
            new()
            {
                Id = "qwen-plus".ToModelId(GetIdentifier()),
                Name = "qwen-plus",
                Type = "language",
                OwnedBy = "Alibaba",
                ContextWindow = 1000000,
              //  Pricing = new ModelPricing { Input = "0.4", Output = "1.2" }
            },
            new()
            {
                Id = "qwen-flash".ToModelId(GetIdentifier()),
                Name = "qwen-flash",
                Type = "language",
                OwnedBy = "Alibaba",
                ContextWindow = 1000000,
             //   Pricing = new ModelPricing { Input = "0.05", Output = "0.4" }
            },
            new()
            {
                Id = "qwen-coder".ToModelId(GetIdentifier()),
                Name = "qwen-coder",
                Type = "language",
                OwnedBy = "Alibaba",
                ContextWindow = 1000000,
             //   Pricing = new ModelPricing { Input = "0.3", Output = "1.5" }
            },

            // ---- Image generation (Qwen-Image) ----
            new()
            {
                Id = "qwen-image-max".ToModelId(GetIdentifier()),
                Name = "qwen-image-max",
                Type = "image",
                OwnedBy = "Alibaba",
              //  Pricing = new ModelPricing { Input = "0.075", Output = "0" }
            },
            new()
            {
                Id = "qwen-image-max-2025-12-30".ToModelId(GetIdentifier()),
                Name = "qwen-image-max-2025-12-30",
                Type = "image",
                OwnedBy = "Alibaba",
             //   Pricing = new ModelPricing { Input = "0.075", Output = "0" }
            },
            new()
            {
                Id = "qwen-image-plus".ToModelId(GetIdentifier()),
                Name = "qwen-image-plus",
                Type = "image",
                OwnedBy = "Alibaba",
             //   Pricing = new ModelPricing { Input = "0.03", Output = "0" }
            },
            new()
            {
                Id = "qwen-image".ToModelId(GetIdentifier()),
                Name = "qwen-image",
                Type = "image",
                OwnedBy = "Alibaba",
            //    Pricing = new ModelPricing { Input = "0.035", Output = "0" }
            },

            // ---- Image generation (Tongyi Z-Image) ----
            new()
            {
                Id = "z-image-turbo".ToModelId(GetIdentifier()),
                Name = "z-image-turbo",
                Type = "image",
                OwnedBy = "Alibaba",
             //   Pricing = new ModelPricing { Input = "0", Output = "0" }
            },

            // ---- Image generation (Wan 2.6) ----
            new()
            {
                Id = "wan2.6-image".ToModelId(GetIdentifier()),
                Name = "wan2.6-image",
                Type = "image",
                OwnedBy = "Alibaba",
             //   Pricing = new ModelPricing { Input = "0", Output = "0" }
            },
            new()
            {
                Id = "wan2.6-t2i".ToModelId(GetIdentifier()),
                Name = "wan2.6-t2i",
                Type = "image",
                OwnedBy = "Alibaba",
            //    Pricing = new ModelPricing { Input = "0", Output = "0" }
            }
        ]);
    }

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    IAsyncEnumerable<ChatCompletionUpdate> IModelProvider.CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }

    public Task<Common.Model.Responses.ResponseResult> ResponsesAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public IAsyncEnumerable<Common.Model.Responses.ResponseStreamPart> ResponsesStreamingAsync(Common.Model.Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }
}

