using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Common.Model.ChatCompletions;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.JassieAI;

public partial class JassieAIProvider
{
    private async Task<ResponseResult> ResponsesCoreAsync(
        ResponseRequest options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: false);
        var payload = BuildNativeRequest(completionOptions, stream: false);
        payload.MaxTokens = options.MaxOutputTokens;
        payload.Web ??= ExtractWebMode(options.Metadata);

        var native = await SendNativeAsync(payload, ResolveEndpoint(payload.Model), cancellationToken);
        var text = native.Content ?? string.Empty;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = native.RequestId ?? Guid.NewGuid().ToString("N"),
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = createdAt,
            Status = string.Equals(native.Type, "error", StringComparison.OrdinalIgnoreCase) ? "failed" : "completed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = payload.Model,
            Temperature = options.Temperature,
            Usage = BuildUsage(native),
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Error = string.Equals(native.Type, "error", StringComparison.OrdinalIgnoreCase)
                ? new ResponseResultError
                {
                    Code = "jassie_error",
                    Message = native.Error?.GetRawText() ?? "JassieAI returned an error response."
                }
                : null,
            Output =
            [
                new
                {
                    id = $"msg_{native.RequestId ?? Guid.NewGuid().ToString("N")}",
                    type = "message",
                    role = "assistant",
                    content = new object[]
                    {
                        new
                        {
                            type = "output_text",
                            text,
                            sources = native.Sources
                        }
                    }
                }
            ]
        };
    }

    private async IAsyncEnumerable<ResponseStreamPart> ResponsesCoreStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var completionOptions = BuildChatOptionsFromResponseRequest(options, stream: true);
        var payload = BuildNativeRequest(completionOptions, stream: true);
        payload.MaxTokens = options.MaxOutputTokens;
        payload.Web ??= ExtractWebMode(options.Metadata);

        var responseId = Guid.NewGuid().ToString("N");
        var itemId = $"msg_{responseId}";
        var sequence = 1;
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var fullText = new StringBuilder();
        var sawChunk = false;
        JassieNativeResponse? lastChunk = null;

        var initial = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = payload.Model,
            Temperature = options.Temperature,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Output = []
        };

        yield return new ResponseCreated { SequenceNumber = sequence++, Response = initial };
        yield return new ResponseInProgress { SequenceNumber = sequence++, Response = initial };

        await foreach (var chunk in StreamNativeAsync(payload, ResolveEndpoint(payload.Model), cancellationToken))
        {
            sawChunk = true;
            lastChunk = chunk;
            responseId = chunk.RequestId ?? responseId;

            var delta = ExtractAssistantText(chunk);
            if (string.IsNullOrEmpty(delta))
                continue;

            fullText.Append(delta);
            yield return new ResponseOutputTextDelta
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                ContentIndex = 0,
                Outputindex = 0,
                Delta = delta
            };
        }

        var finalText = fullText.ToString();
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

        var final = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = sawChunk && !string.Equals(lastChunk?.Type, "error", StringComparison.OrdinalIgnoreCase) ? "completed" : "failed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = payload.Model,
            Temperature = options.Temperature,
            Usage = lastChunk is null ? null : BuildUsage(lastChunk),
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = options.Metadata,
            Error = !sawChunk || string.Equals(lastChunk?.Type, "error", StringComparison.OrdinalIgnoreCase)
                ? new ResponseResultError
                {
                    Code = "jassie_stream_failed",
                    Message = lastChunk?.Error?.GetRawText() ?? "JassieAI stream ended without usable text output."
                }
                : null,
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
                            text = finalText,
                            sources = lastChunk?.Sources
                        }
                    }
                }
            ]
        };

        if (final.Error is null)
            yield return new ResponseCompleted { SequenceNumber = sequence, Response = final };
        else
            yield return new ResponseFailed { SequenceNumber = sequence, Response = final };
    }
}
