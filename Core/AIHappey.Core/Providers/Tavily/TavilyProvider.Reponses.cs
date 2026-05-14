using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Unified.Models;

namespace AIHappey.Core.Providers.Tavily;

public partial class TavilyProvider
{
    private async IAsyncEnumerable<ResponseStreamPart> StreamUnifiedResponsePartsAsync(
        AIRequest unifiedRequest,
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var model = options.Model ?? NormalizeResearchModel(unifiedRequest.Model);
        var responseId = unifiedRequest.Id ?? Guid.NewGuid().ToString("n");
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

        await foreach (var streamEvent in StreamUnifiedAsync(unifiedRequest, cancellationToken))
        {
            if (streamEvent.Event.Type is "text-start" or "text-delta" or "text-end" or "finish" or "data-tavily.structured-output")
            {
                if (!string.IsNullOrWhiteSpace(streamEvent.Event.Id))
                    responseId = streamEvent.Event.Id!;

                if (streamEvent.Event.Timestamp is { } timestamp)
                    createdAt = timestamp.ToUnixTimeSeconds();
            }

            if (string.Equals(streamEvent.Event.Type, "source-url", StringComparison.OrdinalIgnoreCase))
            {
                var source = ToSource(streamEvent);
                if (!string.IsNullOrWhiteSpace(source.Url))
                    sources[source.Url] = source;

                continue;
            }

            if (string.Equals(streamEvent.Event.Type, "text-delta", StringComparison.OrdinalIgnoreCase)
                && streamEvent.Event.Data is AITextDeltaEventData textDelta)
            {
                fullText.Append(textDelta.Delta);

                yield return new ResponseOutputTextDelta
                {
                    SequenceNumber = sequence++,
                    ItemId = $"msg_{responseId}",
                    Outputindex = 0,
                    ContentIndex = 0,
                    Delta = textDelta.Delta
                };

                continue;
            }

            if (TryGetStructuredOutputData(streamEvent, out var structuredData))
            {
                structured = structuredData;
                var deltaText = JsonSerializer.Serialize(structuredData, JsonSerializerOptions.Web);
                fullText.Append(deltaText);

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

    private static ResponseResult ToResponseResult(AIResponse response, ResponseRequest request)
        => ToResponseResult(ToCompletedTask(response), request, response.Model ?? request.Model ?? "auto");

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
