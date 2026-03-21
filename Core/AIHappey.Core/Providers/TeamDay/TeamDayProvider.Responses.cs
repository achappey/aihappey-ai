using System.Runtime.CompilerServices;
using System.Text;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;

namespace AIHappey.Core.Providers.TeamDay;

public partial class TeamDayProvider
{
    public async Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeAgentModelId(options.Model);
        var metadata = ReadResponseMetadata(options.Metadata);
        var prompt = ApplyStructuredOutputInstructions(BuildPromptFromResponseRequest(options), options.Text);
        var result = await ExecuteAgentAsync(model, prompt, metadata, stream: false, cancellationToken);
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        return new ResponseResult
        {
            Id = result.ExecutionId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = createdAt,
            Status = "completed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? model,
            Temperature = options.Temperature,
            Usage = result.Usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = MergeExecutionMetadata(options.Metadata, result),
            Output = BuildResponseOutput(result.ExecutionId, result.Text)
        };
    }

    public async IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);

        var model = NormalizeAgentModelId(options.Model);
        var metadata = ReadResponseMetadata(options.Metadata);
        var prompt = ApplyStructuredOutputInstructions(BuildPromptFromResponseRequest(options), options.Text);

        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sequence = 1;
        var text = new StringBuilder();
        object? usage = null;
        string? failureMessage = null;
        string? chatId = null;
        string? sessionId = null;

        var inProgress = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? model,
            Temperature = options.Temperature,
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

        await foreach (var evt in StreamAgentExecutionAsync(model, prompt, metadata, cancellationToken))
        {
            switch (evt)
            {
                case TeamDayMetaStreamEvent meta:
                    if (!string.IsNullOrWhiteSpace(meta.ExecutionId))
                    {
                        responseId = meta.ExecutionId!;
                        itemId = $"msg_{responseId}";
                    }

                    if (!string.IsNullOrWhiteSpace(meta.ChatId))
                        chatId = meta.ChatId;

                    if (!string.IsNullOrWhiteSpace(meta.SessionId))
                        sessionId = meta.SessionId;
                    break;

                case TeamDayDeltaStreamEvent delta when !string.IsNullOrEmpty(delta.Text):
                    text.Append(delta.Text);
                    yield return new ResponseOutputTextDelta
                    {
                        SequenceNumber = sequence++,
                        ItemId = itemId,
                        Outputindex = 0,
                        ContentIndex = 0,
                        Delta = delta.Text
                    };
                    break;

                case TeamDayResultStreamEvent resultEvent:
                    if (!string.IsNullOrWhiteSpace(resultEvent.SessionId))
                        sessionId = resultEvent.SessionId;

                    if (resultEvent.Usage is not null)
                        usage = resultEvent.Usage;
                    break;

                case TeamDayErrorStreamEvent error:
                    failureMessage = error.Message;
                    yield return new ResponseError
                    {
                        SequenceNumber = sequence++,
                        Message = error.Message,
                        Param = "model",
                        Code = "teamday_stream_error"
                    };
                    break;
            }
        }

        var finalText = text.ToString();
        if (!string.IsNullOrEmpty(finalText))
        {
            yield return new ResponseOutputTextDone
            {
                SequenceNumber = sequence++,
                ItemId = itemId,
                Outputindex = 0,
                ContentIndex = 0,
                Text = finalText
            };
        }

        var result = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Status = failureMessage is null ? "completed" : "failed",
            ParallelToolCalls = options.ParallelToolCalls,
            Model = options.Model ?? model,
            Temperature = options.Temperature,
            Usage = usage,
            Text = options.Text,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Store = options.Store,
            MaxOutputTokens = options.MaxOutputTokens,
            Metadata = MergeExecutionMetadata(options.Metadata, responseId, chatId, sessionId),
            Error = failureMessage is null
                ? null
                : new ResponseResultError
                {
                    Code = "teamday_stream_error",
                    Message = failureMessage
                },
            Output = BuildResponseOutput(responseId, finalText)
        };

        if (failureMessage is null)
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


    private static string BuildPromptFromResponseRequest(ResponseRequest request)
    {
        var lines = new List<string>();

        if (!string.IsNullOrWhiteSpace(request.Instructions))
            lines.Add($"system: {request.Instructions}");

        if (request.Input?.IsText == true && !string.IsNullOrWhiteSpace(request.Input.Text))
            lines.Add($"user: {request.Input.Text}");

        if (request.Input?.IsItems == true && request.Input.Items is not null)
        {
            foreach (var item in request.Input.Items.OfType<ResponseInputMessage>())
            {
                var text = FlattenResponseMessageContent(item.Content);
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                var role = item.Role switch
                {
                    ResponseRole.Assistant => "assistant",
                    ResponseRole.System => "system",
                    ResponseRole.Developer => "system",
                    _ => "user"
                };

                lines.Add($"{role}: {text}");
            }
        }

        return string.Join("\n\n", lines);
    }


    private static string FlattenResponseMessageContent(ResponseMessageContent content)
    {
        if (content.IsText)
            return content.Text ?? string.Empty;

        if (!content.IsParts || content.Parts is null)
            return string.Empty;

        var parts = new List<string>();
        foreach (var part in content.Parts)
        {
            switch (part)
            {
                case InputTextPart textPart when !string.IsNullOrWhiteSpace(textPart.Text):
                    parts.Add(textPart.Text);
                    break;

                case InputImagePart imagePart:
                    parts.Add(imagePart.ImageUrl ?? imagePart.FileId ?? "[input_image]");
                    break;

                case InputFilePart filePart:
                    parts.Add(filePart.FileUrl ?? filePart.FileId ?? filePart.Filename ?? "[input_file]");
                    break;
            }
        }

        return string.Join("\n", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
    }



    private static IEnumerable<object> BuildResponseOutput(string id, string text)
    {
        return
        [
            new
            {
                id = $"msg_{id}",
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
        ];
    }


    private static TeamDayRequestMetadata ReadResponseMetadata(Dictionary<string, object?>? metadata)
    {
        if (metadata is null)
            return new TeamDayRequestMetadata();

        if (metadata.TryGetValue(nameof(TeamDay).ToLowerInvariant(), out var nested) && TryDeserializeMetadata(nested) is { } direct)
            return direct;

        return new TeamDayRequestMetadata
        {
            SpaceId = metadata.TryGetValue("teamday:spaceId", out var spaceId) ? spaceId?.ToString() : null,
            SessionId = metadata.TryGetValue("teamday:sessionId", out var sessionId) ? sessionId?.ToString() : null,
            ChatId = metadata.TryGetValue("teamday:chatId", out var chatId) ? chatId?.ToString() : null
        };
    }


}
