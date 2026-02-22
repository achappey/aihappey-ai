using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Responses.Extensions;

namespace AIHappey.Core.Providers.Exa;

public partial class ExaProvider
{
    public async Task<Responses.ResponseResult> ResponsesAsync(Responses.ResponseRequest options, CancellationToken cancellationToken = default)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();
            return await CompleteResearchResponsesAsync(options, model, cancellationToken);
        }

        if (!IsResearchModel(model))
            throw new NotSupportedException($"Exa responses only support research models. Requested: '{options?.Model}'.");

        ApplyResearchAuthHeader();
        return await _client.GetResponses(options, relativeUrl: "responses", ct: cancellationToken);
    }


    public async IAsyncEnumerable<Responses.Streaming.ResponseStreamPart> ResponsesStreamingAsync(Responses.ResponseRequest options,
       [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var model = options?.Model ?? throw new ArgumentException("Model missing", nameof(options));

        if (IsResearchFastModel(model))
        {
            ApplyResearchAuthHeader();

            await foreach (var part in CompleteResearchResponsesStreamingAsync(options, model, cancellationToken))
                yield return part;

            yield break;
        }

        if (!IsResearchModel(model))
            throw new NotSupportedException($"Exa responses only support research models. Requested: '{options?.Model}'.");

        ApplyResearchAuthHeader();

        await foreach (var update in _client.GetResponsesUpdates(options, relativeUrl: "responses", ct: cancellationToken))
            yield return update;
    }

    private async IAsyncEnumerable<ResponseStreamPart> CompleteResearchResponsesStreamingAsync(
        ResponseRequest options,
        string model,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var input = BuildPromptFromResponseRequest(options);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Exa research requires non-empty input for responses.");

        var outputSchema = TryExtractOutputSchema(options.Text);
        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var fullText = new StringBuilder();
        object? structured = null;
        var sequence = 1;

        var initial = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = model,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = initial
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = initial
        };

        await foreach (var evt in StreamResearchEventsAsync(input, model, outputSchema, cancellationToken))
        {
            if (evt.Created > 0)
                createdAt = evt.Created;

            string? deltaText = null;
            if (!string.IsNullOrWhiteSpace(evt.ContentText))
            {
                deltaText = evt.ContentText;
                fullText.Append(deltaText);
            }
            else if (evt.ContentObject is not null)
            {
                structured = JsonSerializer.Deserialize<object>(evt.ContentObject.Value.GetRawText(), JsonSerializerOptions.Web);
                deltaText = evt.ContentObject.Value.GetRawText();
                fullText.Append(deltaText);
            }

            if (!string.IsNullOrEmpty(deltaText))
            {
                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = $"msg_{responseId}",
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = deltaText
                };
            }
        }

        var finalText = fullText.ToString();
        if (string.IsNullOrWhiteSpace(finalText) && structured is not null)
            finalText = JsonSerializer.Serialize(structured, JsonSerializerOptions.Web);

        var completed = new ExaResearchCompletedTask
        {
            ResearchId = responseId,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
            FinishedAt = DateTime.UtcNow,
            Content = structured ?? finalText,
            Parsed = structured
        };

        var finalResult = ToResponseResult(completed, options, model);

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = sequence++,
            ItemId = $"msg_{finalResult.Id}",
            Outputindex = 0,
            ContentIndex = 0,
            Text = finalText
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = sequence,
            Response = finalResult
        };
    }

    private async Task<ResponseResult> CompleteResearchResponsesAsync(
        ResponseRequest options,
        string model,
        CancellationToken cancellationToken)
    {
        var input = BuildPromptFromResponseRequest(options);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Exa research requires non-empty input for responses.");

        var outputSchema = TryExtractOutputSchema(options.Text);
        var queued = await QueueResearchTaskAsync(input, model, outputSchema, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.ResearchId, cancellationToken);

        return ToResponseResult(completed, options, model);
    }

    private static ResponseResult ToResponseResult(ExaResearchCompletedTask completed, ResponseRequest request, string model)
    {
        var text = ToOutputText(completed.Parsed ?? completed.Content);
        var created = ToUnixTimeSeconds(completed.CreatedAt);
        var completedAt = ToUnixTimeSeconds(completed.FinishedAt);

        return new ResponseResult
        {
            Id = completed.ResearchId,
            Object = "response",
            CreatedAt = created,
            CompletedAt = completedAt,
            Status = "completed",
            Model = model,
            Temperature = request.Temperature,
            Metadata = request.Metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Output =
            [
                new
                {
                    id = $"msg_{completed.ResearchId}",
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
}
