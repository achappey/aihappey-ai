using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses.Extensions;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.InceptionLabs;

public partial class InceptionLabsProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public InceptionLabsProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.inceptionlabs.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(InceptionLabs)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
          => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));

    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var relativeUrl = ResolveChatCompletionsRelativeUrl(options.Model);

        return await _client.GetChatCompletion(
             options, relativeUrl: relativeUrl, ct: cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        var relativeUrl = ResolveChatCompletionsRelativeUrl(options.Model);

        return _client.GetChatCompletionUpdates(
                    options, relativeUrl: relativeUrl, ct: cancellationToken);
    }

    private static string ResolveChatCompletionsRelativeUrl(string? model)
    {
        var normalizedModel = model ?? string.Empty;

        if (normalizedModel.Equals("mercury-edit/apply", StringComparison.OrdinalIgnoreCase))
            return "v1/apply/completions";

        if (normalizedModel.StartsWith("mercury-edit", StringComparison.OrdinalIgnoreCase))
            return "v1/edit/completions";

        // Keep Mercury 2 and other models on standard chat completions.
        return "v1/chat/completions";
    }
    private static bool IsMercuryEditModel(string? model)
        => model?.StartsWith("mercury-edit", StringComparison.OrdinalIgnoreCase) == true;

    public string GetIdentifier() => nameof(InceptionLabs).ToLowerInvariant();

    public Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
    {
        throw new NotImplementedException();
    }

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!IsMercuryEditModel(options.Model))
            return _client.GetResponses(options, ct: cancellationToken);

        return CompleteMercuryEditResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (!IsMercuryEditModel(options.Model))
            return _client.GetResponsesUpdates(options, ct: cancellationToken);

        return CompleteMercuryEditResponsesStreamingAsync(options, cancellationToken);
    }

    private async Task<Responses.ResponseResult> CompleteMercuryEditResponsesAsync(
        Responses.ResponseRequest options,
        CancellationToken cancellationToken)
    {
        var chatOptions = ToMercuryEditChatCompletionOptions(options, stream: false);
        var relativeUrl = ResolveChatCompletionsRelativeUrl(options.Model);

        var completion = await _client.GetChatCompletion(
            chatOptions,
            relativeUrl: relativeUrl,
            ct: cancellationToken);

        var text = ExtractAssistantText(completion);
        return BuildResponseResult(completion.Id, completion.Created, completion.Model, text, completion.Usage, options);
    }

    private async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> CompleteMercuryEditResponsesStreamingAsync(
        Responses.ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chatOptions = ToMercuryEditChatCompletionOptions(options, stream: true);
        var relativeUrl = ResolveChatCompletionsRelativeUrl(options.Model);

        var responseId = $"resp_{Guid.NewGuid():N}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var itemId = $"msg_{responseId}";
        var sequenceNumber = 1;
        var content = new StringBuilder();
        object? usage = null;

        var inProgress = new Responses.ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = chatOptions.Model,
            Temperature = options.Temperature,
            ParallelToolCalls = options.ParallelToolCalls,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            Metadata = options.Metadata
        };

        yield return new Responses.Streaming.ResponseCreated
        {
            SequenceNumber = sequenceNumber++,
            Response = inProgress
        };

        yield return new Responses.Streaming.ResponseInProgress
        {
            SequenceNumber = sequenceNumber++,
            Response = inProgress
        };

        await foreach (var update in _client.GetChatCompletionUpdates(
            chatOptions,
            relativeUrl: relativeUrl,
            ct: cancellationToken).ConfigureAwait(false))
        {
            var delta = ExtractDeltaText(update);
            if (string.IsNullOrEmpty(delta))
                continue;

            content.Append(delta);
            usage = update.Usage ?? usage;

            yield return new Responses.Streaming.ResponseOutputTextDelta
            {
                SequenceNumber = sequenceNumber++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Delta = delta
            };
        }

        var finalText = content.ToString();

        yield return new Responses.Streaming.ResponseOutputTextDone
        {
            SequenceNumber = sequenceNumber++,
            ItemId = itemId,
            Outputindex = 0,
            ContentIndex = 0,
            Text = finalText
        };

        var completed = BuildResponseResult(responseId, createdAt, chatOptions.Model, finalText, usage, options);

        yield return new Responses.Streaming.ResponseCompleted
        {
            SequenceNumber = sequenceNumber,
            Response = completed
        };
    }

    private static ChatCompletionOptions ToMercuryEditChatCompletionOptions(Responses.ResponseRequest options, bool stream)
    {
        var prompt = BuildMercuryEditPromptFromResponseRequest(options);

        return new ChatCompletionOptions
        {
            Model = "mercury-edit",
            Temperature = options.Temperature,
            Stream = stream,
            Messages =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = JsonSerializer.SerializeToElement(prompt)
                }
            ]
        };
    }

    private static string BuildMercuryEditPromptFromResponseRequest(Responses.ResponseRequest options)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(options.Instructions))
            sb.AppendLine(options.Instructions.Trim());

        if (options.Input?.IsText == true && !string.IsNullOrWhiteSpace(options.Input.Text))
            sb.AppendLine(options.Input.Text);

        if (options.Input?.IsItems == true && options.Input.Items is not null)
        {
            foreach (var item in options.Input.Items)
            {
                if (item is not Responses.ResponseInputMessage message)
                    continue;

                var role = message.Role.ToString().ToLowerInvariant();

                if (message.Content.IsText && !string.IsNullOrWhiteSpace(message.Content.Text))
                {
                    sb.AppendLine($"{role}: {message.Content.Text}");
                    continue;
                }

                if (message.Content.IsParts && message.Content.Parts is not null)
                {
                    foreach (var part in message.Content.Parts)
                    {
                        if (part is Responses.InputTextPart textPart && !string.IsNullOrWhiteSpace(textPart.Text))
                            sb.AppendLine($"{role}: {textPart.Text}");
                    }
                }
            }
        }

        return sb.ToString().Trim();
    }

    private static string ExtractAssistantText(ChatCompletion completion)
    {
        foreach (var choice in completion.Choices)
        {
            if (choice is not JsonElement choiceEl || choiceEl.ValueKind != JsonValueKind.Object)
                continue;

            if (!choiceEl.TryGetProperty("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.Object)
                continue;

            if (messageEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string ExtractDeltaText(ChatCompletionUpdate update)
    {
        foreach (var choice in update.Choices)
        {
            if (choice is not JsonElement choiceEl || choiceEl.ValueKind != JsonValueKind.Object)
                continue;

            if (!choiceEl.TryGetProperty("delta", out var deltaEl) || deltaEl.ValueKind != JsonValueKind.Object)
                continue;

            if (deltaEl.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static Responses.ResponseResult BuildResponseResult(
        string responseId,
        long createdAt,
        string model,
        string text,
        object? usage,
        Responses.ResponseRequest source)
    {
        return new Responses.ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = "completed",
            Model = model,
            Temperature = source.Temperature,
            ParallelToolCalls = source.ParallelToolCalls,
            MaxOutputTokens = source.MaxOutputTokens,
            Store = source.Store,
            Metadata = source.Metadata,
            Usage = usage,
            Output =
            [
                new
                {
                    id = $"msg_{responseId}",
                    type = "message",
                    role = "assistant",
                    content = new object[]
                    {
                        new
                        {
                            type = "output_text",
                            text
                        }
                    }
                }
            ]
        };
    }

    public Task<RealtimeResponse> GetRealtimeToken(RealtimeRequest realtimeRequest, CancellationToken cancellationToken)
        => throw new NotSupportedException();

    public Task<ImageResponse> ImageRequest(ImageRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<VideoResponse> VideoRequest(VideoRequest request, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException();
    }
}
