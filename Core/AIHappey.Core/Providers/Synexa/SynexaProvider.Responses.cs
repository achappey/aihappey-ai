using System.Text.Json;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.Synexa;

public partial class SynexaProvider
{
    public async Task<ResponseResult> ResponsesAsync(
        ResponseRequest options,
        CancellationToken cancellationToken = default)
    {
        var model = options.Model;
        if (string.IsNullOrWhiteSpace(model))
            throw new InvalidOperationException("Model is required.");

        var prompt = BuildPromptFromResponseInput(options.Input);
        if (string.IsNullOrWhiteSpace(prompt))
            prompt = options.Instructions ?? string.Empty;

        var prediction = await CreatePredictionAsync(
            model,
            new Dictionary<string, object?>
            {
                ["prompt"] = prompt,
                ["temperature"] = options.Temperature,
                ["max_tokens"] = options.MaxOutputTokens,
                ["system_prompt"] = options.Instructions
            },
            cancellationToken);

        var completed = await WaitPredictionAsync(prediction, wait: null, cancellationToken);
        var text = ExtractOutputText(completed.Output);
        var createdAt = ParseTimestampOrNow(completed.CreatedAt).ToUnixTimeSeconds();
        var completedAt = ParseTimestampOrNow(completed.CompletedAt).ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = completed.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = "completed",
            Model = model,
            Temperature = options.Temperature,
            Output =
            [
                new
                {
                    id = $"msg_{completed.Id}",
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
            ],
            Metadata = options.Metadata,
            Usage = completed.Metrics.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
                ? null
                : completed.Metrics.Clone()
        };
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var result = await ResponsesAsync(options, cancellationToken);

        yield return new ResponseCreated
        {
            SequenceNumber = 1,
            Response = result
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = 2,
            Response = new ResponseResult
            {
                Id = result.Id,
                Object = result.Object,
                CreatedAt = result.CreatedAt,
                CompletedAt = result.CompletedAt,
                Status = "in_progress",
                ParallelToolCalls = result.ParallelToolCalls,
                Model = result.Model,
                Temperature = result.Temperature,
                Output = result.Output,
                Usage = result.Usage,
                Text = result.Text,
                ToolChoice = result.ToolChoice,
                Tools = result.Tools,
                Reasoning = result.Reasoning,
                Store = result.Store,
                MaxOutputTokens = result.MaxOutputTokens,
                Error = result.Error,
                Metadata = result.Metadata
            }
        };

        var text = string.Empty;
        var first = result.Output.FirstOrDefault();
        if (first is not null)
        {
            var json = JsonSerializer.SerializeToElement(first, JsonSerializerOptions.Web);
            if (json.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.Array)
            {
                var firstContent = contentEl.EnumerateArray().FirstOrDefault();
                if (firstContent.ValueKind == JsonValueKind.Object
                    && firstContent.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    text = textEl.GetString() ?? string.Empty;
                }
            }
        }

        yield return new ResponseOutputTextDelta
        {
            SequenceNumber = 3,
            ItemId = $"msg_{result.Id}",
            Outputindex = 0,
            ContentIndex = 0,
            Delta = text
        };

        yield return new ResponseOutputTextDone
        {
            SequenceNumber = 4,
            ItemId = $"msg_{result.Id}",
            Outputindex = 0,
            ContentIndex = 0,
            Text = text
        };

        yield return new ResponseCompleted
        {
            SequenceNumber = 5,
            Response = result
        };
    }
}

