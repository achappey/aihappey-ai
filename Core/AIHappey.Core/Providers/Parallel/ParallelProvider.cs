using AIHappey.Core.AI;
using ModelContextProtocol.Protocol;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Common.Model;
using AIHappey.Vercel.Models;
using AIHappey.Core.Contracts;
using AIHappey.Core.Models;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Parallel;

public partial class ParallelProvider : IModelProvider
{
    private readonly IApiKeyResolver _keyResolver;

    private readonly HttpClient _client;

    public ParallelProvider(IApiKeyResolver keyResolver, IHttpClientFactory httpClientFactory)
    {
        _keyResolver = keyResolver;
        _client = httpClientFactory.CreateClient();
        _client.BaseAddress = new Uri("https://api.parallel.ai/");
    }

    private void ApplyAuthHeader()
    {
        var key = _keyResolver.Resolve(GetIdentifier());

        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"No {nameof(Parallel)} API key.");

        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        _client.DefaultRequestHeaders.Remove("x-api-key");
        _client.DefaultRequestHeaders.Add("x-api-key", key);
    }

    public async Task<IEnumerable<Model>> ListModels(CancellationToken cancellationToken = default)
        => await this.ListModels(_keyResolver.Resolve(GetIdentifier()));


    public async Task<ChatCompletion> CompleteChatAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return await CompleteChatInternalAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ChatCompletionUpdate> CompleteChatStreamingAsync(ChatCompletionOptions options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return CompleteChatStreamingInternalAsync(options, cancellationToken);
    }

    public string GetIdentifier() => nameof(Parallel).ToLowerInvariant();

    public async Task<CreateMessageResult> SamplingAsync(CreateMessageRequestParams chatRequest, CancellationToken cancellationToken = default)
        => await this.ChatCompletionsSamplingAsync(chatRequest, cancellationToken);

    public Task<TranscriptionResponse> TranscriptionRequest(TranscriptionRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<SpeechResponse> SpeechRequest(SpeechRequest imageRequest, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<RerankingResponse> RerankingRequest(RerankingRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: false);
        var completion = await CompleteChatInternalAsync(completionOptions, cancellationToken);

        var text = ExtractAssistantTextFromChoices(completion.Choices);
        var createdAt = completion.Created > 0 ? completion.Created : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var completedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = completion.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = "completed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = completion.Model,
            Temperature = options.Temperature,
            Usage = completion.Usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output =
            [
                new
                {
                    id = $"msg_{completion.Id}",
                    type = "message",
                    role = "assistant",
                    content = new[]
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

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: true);
        var itemId = $"msg_{Guid.NewGuid():N}";

        var responseId = Guid.NewGuid().ToString("N");
        var model = completionOptions.Model;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;
        var text = new System.Text.StringBuilder();
        object? usage = null;
        bool hasAnyChunk = false;

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = model,
            Temperature = options.Temperature,
            Usage = null,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output = []
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgress
        };

        await foreach (var rawChunk in StreamChatRawChunksAsync(completionOptions, cancellationToken))
        {
            hasAnyChunk = true;
            using var doc = System.Text.Json.JsonDocument.Parse(rawChunk);
            var root = doc.RootElement;

            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                responseId = idEl.GetString() ?? responseId;

            if (root.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == System.Text.Json.JsonValueKind.String)
                model = modelEl.GetString() ?? model;

            if (root.TryGetProperty("created", out var createdEl) && createdEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                createdAt = createdEl.GetInt64();

            if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind is not System.Text.Json.JsonValueKind.Null and not System.Text.Json.JsonValueKind.Undefined)
                usage = System.Text.Json.JsonSerializer.Deserialize<object>(usageEl.GetRawText());

            if (!root.TryGetProperty("choices", out var choicesEl) || choicesEl.ValueKind != System.Text.Json.JsonValueKind.Array)
                continue;

            foreach (var choice in choicesEl.EnumerateArray())
            {
                if (choice.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.ValueKind == System.Text.Json.JsonValueKind.Object
                    && deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var delta = contentEl.GetString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        text.Append(delta);
                        yield return new ResponseOutputTextDelta
                        {
                            SequenceNumber = sequence++,
                            ItemId = itemId,
                            ContentIndex = 0,
                            Outputindex = 0,
                            Delta = delta
                        };
                    }
                }
            }
        }

        var finalText = text.ToString();

        if (!string.IsNullOrEmpty(finalText))
        {
            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Text = finalText
            };
        }

        var result = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = hasAnyChunk ? "completed" : "failed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = model,
            Temperature = options.Temperature,
            Usage = usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Error = hasAnyChunk
                ? null
                : new ResponseResultError
                {
                    Code = "parallel_empty_stream",
                    Message = "Parallel stream ended without any completion chunks."
                },
            Output =
            [
                new
                {
                    id = itemId,
                    type = "message",
                    role = "assistant",
                    content = new[]
                    {
                        new
                        {
                            type = "output_text",
                            text = finalText
                        }
                    }
                }
            ]
        };

        if (hasAnyChunk)
        {
            yield return new ResponseCompleted
            {
                SequenceNumber = sequence,
                Response = result
            };
        }
        else
        {
            yield return new ResponseFailed
            {
                SequenceNumber = sequence,
                Response = result
            };
        }
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
