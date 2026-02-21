using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private async IAsyncEnumerable<ResponseStreamPart> CompleteResponsesStreamingAsync(
            ResponseRequest options,
            [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = options.Model;
        var input = BuildPromptFromResponseRequest(options);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input for responses.");

        var outputSchema = TryExtractOutputSchema(options.Text);
        var responseId = Guid.NewGuid().ToString("n");
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var sources = new Dictionary<string, TavilySource>(StringComparer.OrdinalIgnoreCase);
        var fullText = new StringBuilder();
        object? structured = null;

        var sequence = 1;
        var initial = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = model!,
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

        await foreach (var evt in StreamResearchEventsAsync(input, model!, outputSchema, cancellationToken))
        {
            if (!string.IsNullOrWhiteSpace(evt.Id))
                responseId = evt.Id!;

            if (evt.Created > 0)
                createdAt = evt.Created;

            foreach (var source in evt.Sources)
            {
                if (!string.IsNullOrWhiteSpace(source.Url))
                    sources[source.Url] = source;
            }

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

        var completed = new TavilyCompletedTask
        {
            RequestId = responseId,
            CreatedAt = DateTimeOffset.FromUnixTimeSeconds(createdAt).UtcDateTime,
            Content = structured ?? finalText,
            Sources = [.. sources.Values],
            ResponseTime = 0
        };

        var finalResult = ToResponseResult(completed, options, model!);

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

    private async Task<ResponseResult> CompleteResponsesAsync(
            ResponseRequest options,
            CancellationToken cancellationToken)
    {
        var model = options.Model;
        var input = BuildPromptFromResponseRequest(options);

        if (string.IsNullOrWhiteSpace(input))
            throw new InvalidOperationException("Tavily requires non-empty input for responses.");

        var outputSchema = TryExtractOutputSchema(options.Text);

        var queued = await QueueResearchTaskAsync(input, model!, stream: false, outputSchema, cancellationToken);
        var completed = await WaitForResearchCompletionAsync(queued.RequestId, cancellationToken);

        return ToResponseResult(completed, options, model!);
    }
    private static ResponseResult ToResponseResult(TavilyCompletedTask completed, ResponseRequest request, string model)
    {
        var text = ToOutputText(completed.Content);
        var metadata = MergeMetadata(request.Metadata, BuildSourcesMetadata(completed.Sources));
        var created = ToUnixTime(completed.CreatedAt);
        var completedAt = created + (long)Math.Max(0, Math.Ceiling(completed.ResponseTime));

        return new ResponseResult
        {
            Id = completed.RequestId,
            Object = "response",
            CreatedAt = created,
            CompletedAt = completedAt,
            Status = "completed",
            Model = model,
            Temperature = request.Temperature,
            Metadata = metadata,
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                response_time = completed.ResponseTime,
                prompt_tokens = 0,
                completion_tokens = 0,
                total_tokens = 0
            },
            Output =
            [
                new
                {
                    id = $"msg_{completed.RequestId}",
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

