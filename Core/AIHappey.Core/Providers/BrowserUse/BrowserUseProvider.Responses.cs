using System.Runtime.CompilerServices;
using AIHappey.Responses;
using AIHappey.Responses.Streaming;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public Task<ResponseResult> ResponsesAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesAsync(options, cancellationToken);
    }

    public IAsyncEnumerable<ResponseStreamPart> ResponsesStreamingAsync(ResponseRequest options, CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteResponsesStreamingAsync(options, cancellationToken);
    }

    private async Task<ResponseResult> ExecuteResponsesAsync(ResponseRequest options, CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var metadata = options.Metadata?.GetProviderMetadata<BrowserUseRequestMetadata>(GetIdentifier()) ?? new BrowserUseRequestMetadata();

        var terminal = await ExecuteNativeTaskAsync(new BrowserUseCreateSessionRequest
        {
            Task = prompt,
            Model = options.Model,
            MaxCostUsd = metadata.MaxCostUsd,
            ProfileId = metadata.ProfileId,
            WorkspaceId = metadata.WorkspaceId,
            ProxyCountryCode = metadata.ProxyCountryCode,
            OutputSchema = TryExtractStructuredOutputSchema(options.Text),
            EnableRecording = metadata.EnableRecording ?? false,
            Skills = metadata.Skills,
            Agentmail = metadata.Agentmail,
            CacheScript = metadata.CacheScript
        }, cancellationToken);

        return ToResponseResult(terminal, options);
    }

    private async IAsyncEnumerable<ResponseStreamPart> ExecuteResponsesStreamingAsync(
        ResponseRequest options,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromResponseRequest(options);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var responseId = Guid.NewGuid().ToString("n");
        var itemId = $"msg_{responseId}";
        var sequence = 1;
        var metadata = options.Metadata?.GetProviderMetadata<BrowserUseRequestMetadata>(GetIdentifier()) ?? new BrowserUseRequestMetadata();

        var inProgressResponse = new ResponseResult
        {
            Id = responseId,
            Object = "response",
            CreatedAt = createdAt,
            Status = "in_progress",
            Model = options.Model!,
            Temperature = options.Temperature,
            Metadata = options.Metadata,
            MaxOutputTokens = options.MaxOutputTokens,
            Store = options.Store,
            ToolChoice = options.ToolChoice,
            Tools = options.Tools?.Cast<object>() ?? [],
            Text = options.Text,
            ParallelToolCalls = options.ParallelToolCalls
        };

        yield return new ResponseCreated
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };

        yield return new ResponseInProgress
        {
            SequenceNumber = sequence++,
            Response = inProgressResponse
        };

        await foreach (var evt in StreamNativeTaskAsync(new BrowserUseCreateSessionRequest
        {
            Task = prompt,
            Model = options.Model,
            MaxCostUsd = metadata.MaxCostUsd,
            ProfileId = metadata.ProfileId,
            WorkspaceId = metadata.WorkspaceId,
            ProxyCountryCode = metadata.ProxyCountryCode,
            OutputSchema = TryExtractStructuredOutputSchema(options.Text),
            EnableRecording = metadata.EnableRecording ?? false,
            Skills = metadata.Skills,
            Agentmail = metadata.Agentmail,
            CacheScript = metadata.CacheScript
        }, cancellationToken))
        {
            switch (evt)
            {
                case BrowserUseNativeCreatedStreamEvent created:
                    responseId = created.Created.Id;
                    itemId = $"msg_{responseId}";
                    break;

                case BrowserUseNativeActionStreamEvent actionEvent:
                    {
                        var delta = FormatActionDelta(actionEvent.Action);
                        if (string.IsNullOrWhiteSpace(delta))
                            break;

                        yield return new ResponseOutputTextDelta
                        {
                            SequenceNumber = sequence++,
                            ItemId = itemId,
                            Outputindex = 0,
                            ContentIndex = 0,
                            Delta = delta
                        };
                        break;
                    }

                case BrowserUseNativeTerminalStreamEvent terminalEvent:
                    {
                        var finalText = terminalEvent.Terminal.OutputText;
                        yield return new ResponseOutputTextDone
                        {
                            SequenceNumber = sequence++,
                            ItemId = itemId,
                            Outputindex = 0,
                            ContentIndex = 0,
                            Text = finalText
                        };

                        var finalResult = ToResponseResult(terminalEvent.Terminal, options);

                        if (IsFinished(terminalEvent.Terminal.Session.Status)
                            && terminalEvent.Terminal.Session.IsTaskSuccessful != false)
                        {
                            yield return new ResponseCompleted
                            {
                                SequenceNumber = sequence,
                                Response = finalResult
                            };
                        }
                        else
                        {
                            yield return new ResponseFailed
                            {
                                SequenceNumber = sequence,
                                Response = finalResult
                            };
                        }

                        yield break;
                    }
            }
        }
    }

    private static string FormatActionDelta(BrowserUseNativeActionEvent action)
    {
        if (!string.IsNullOrWhiteSpace(action.DoneText))
            return $"Step {action.StepNumber}: {action.DoneText}.\n";

        return $"Step {action.StepNumber}: {action.ToolName}.\n";
    }

    private ResponseResult ToResponseResult(BrowserUseNativeTerminalResult terminal, ResponseRequest request)
    {
        var session = terminal.Session;
        var text = terminal.OutputText;
        var createdAt = ToUnixTime(session.CreatedAt);
        var completedAt = ParseUnixTimeOrNow(session.UpdatedAt);
        var isCompleted = IsFinished(session.Status) && session.IsTaskSuccessful != false;

        return new ResponseResult
        {
            Id = session.Id,
            Object = "response",
            CreatedAt = createdAt,
            CompletedAt = completedAt,
            Status = isCompleted ? "completed" : "failed",
            Model = request.Model?.ToModelId(GetIdentifier()) ?? session.Model.ToModelId(GetIdentifier()),
            Temperature = request.Temperature,
            Metadata = MergeMetadata(request.Metadata, session),
            MaxOutputTokens = request.MaxOutputTokens,
            Store = request.Store,
            ToolChoice = request.ToolChoice,
            Tools = request.Tools?.Cast<object>() ?? [],
            Text = request.Text,
            ParallelToolCalls = request.ParallelToolCalls,
            Usage = new
            {
                cost = session.TotalCostUsd,
                prompt_tokens = session.TotalInputTokens,
                completion_tokens = session.TotalOutputTokens,
                total_tokens = session.TotalInputTokens + session.TotalOutputTokens
            },
            Error = isCompleted
                ? null
                : new ResponseResultError
                {
                    Code = $"browseruse_session_{session.Status}",
                    Message = string.IsNullOrWhiteSpace(text)
                        ? $"BrowserUse session ended with status '{session.Status}'."
                        : text
                },
            Output =
            [
                new
                {
                    id = $"msg_{session.Id}",
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

    private static Dictionary<string, object?> MergeMetadata(
        Dictionary<string, object?>? current,
        BrowserUseSessionResponse session)
    {
        var merged = current is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(current);

        merged["browseruse_session_id"] = session.Id;
        merged["browseruse_status"] = session.Status;
        merged["browseruse_is_success"] = session.IsTaskSuccessful;
        merged["browseruse_live_url"] = session.LiveUrl;
        merged["browseruse_recording_urls"] = session.RecordingUrls;
        merged["browseruse_total_cost_usd"] = session.TotalCostUsd;
        merged["browseruse_llm_cost_usd"] = session.LlmCostUsd;
        merged["browseruse_proxy_cost_usd"] = session.ProxyCostUsd;
        merged["browseruse_browser_cost_usd"] = session.BrowserCostUsd;
        merged["browseruse_total_input_tokens"] = session.TotalInputTokens;
        merged["browseruse_total_output_tokens"] = session.TotalOutputTokens;
        merged["browseruse_step_count"] = session.StepCount;
        merged["browseruse_last_step_summary"] = session.LastStepSummary;
        merged["gateway"] = CreateGatewayCostMetadata(session);

        return merged;
    }
}

