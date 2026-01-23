using System.Runtime.CompilerServices;
using AIHappey.Common.Model;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Core.AI;
using AIHappey.Core.ModelProviders;
using AIHappey.Core.Models;
using ModelContextProtocol.Protocol;

namespace AIHappey.Core.Providers.MurfAI;

public sealed partial class MurfAIProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;
    private readonly HttpClient _client;

    public MurfAIProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.murf.ai/");
    }

    public string GetIdentifier() => "murfai";

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("No MurfAI API key.");

        // Murf uses a custom api-key header.
        _client.DefaultRequestHeaders.Remove("api-key");
        _client.DefaultRequestHeaders.Add("api-key", key);
    }

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        
        ArgumentNullException.ThrowIfNull(options);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var result = await TranslateAsync(texts, options.Model.Split("/").Last()!, cancellationToken);
        var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

        return new ChatCompletion
        {
            Id = Guid.NewGuid().ToString("n"),
            Created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Model = options.Model,
            Choices = new object[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = joined },
                    finish_reason = "stop"
                }
            },
            Usage = null
        };
    }

    public async IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(
        ChatCompletionOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(options);

        var texts = options.Messages
            .Where(m => string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))
            .Select(m => ChatMessageContentExtensions.ToText(m.Content))
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!)
            .ToList();

        if (texts.Count == 0)
            throw new ArgumentException("No user text provided.", nameof(options));

        var id = Guid.NewGuid().ToString("n");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var seq = 0;

        // First chunk: role
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices = new object[]
            {
                new { index = 0, delta = new { role = "assistant" }, finish_reason = (string?)null }
            }
        };

        var result = await TranslateAsync(texts, options.Model.Split("/").Last()!, cancellationToken);

        for (var i = 0; i < result.Translations.Count; i++)
        {
            var t = result.Translations[i].TranslatedText;
            var piece = (i == result.Translations.Count - 1) ? t : (t + "\n");

            yield return new ChatCompletionUpdate
            {
                Id = id,
                Created = created,
                Model = options.Model,
                Choices = new object[]
                {
                    new { index = 0, delta = new { content = piece }, finish_reason = (string?)null }
                }
            };

            seq++;
        }

        // Final chunk
        yield return new ChatCompletionUpdate
        {
            Id = id,
            Created = created,
            Model = options.Model,
            Choices = new object[]
            {
                new { index = 0, delta = new { }, finish_reason = "stop" }
            }
        };
    }

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest request, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        return MurfAIModels;
    }

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var modelId = chatRequest.GetModel();
        var model = await this.GetModel(modelId, cancellationToken: cancellationToken);

        // translate model
        if (model.Type == "language")
        {
            var texts = chatRequest.Messages
                .Where(m => m.Role == ModelContextProtocol.Protocol.Role.User)
                .SelectMany(m => m.Content.OfType<TextContentBlock>())
                .Select(b => b.Text)
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .ToList();

            if (texts.Count == 0)
                throw new Exception("No prompt provided.");

            var result = await TranslateAsync(texts, modelId?.Split("/").Last()!, cancellationToken);
            var joined = string.Join("\n", result.Translations.Select(t => t.TranslatedText));

            return new CreateMessageResult
            {
                Role = ModelContextProtocol.Protocol.Role.Assistant,
                Model = modelId!,
                StopReason = "stop",
                Content = [joined.ToTextContentBlock()]
            };
        }

        // fallback: speech sampling
        return await this.SpeechSamplingAsync(chatRequest, cancellationToken);
    }

    public async IAsyncEnumerable<UIMessagePart> StreamAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = await this.GetModel(chatRequest.Model, cancellationToken: cancellationToken);
        if (model.Type == "language")
        {
            await foreach (var p in StreamTranslateAsync(chatRequest, cancellationToken))
                yield return p;

            yield break;
        }

        await foreach (var p in this.StreamSpeechAsync(chatRequest, cancellationToken))
            yield return p;
    }
}

