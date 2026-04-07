using AIHappey.Common.Model;
using AIHappey.Common.Extensions;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using ModelContextProtocol.Protocol;
using AIHappey.Core.AI;

namespace AIHappey.Core.Providers.BrowserUse;

public partial class BrowserUseProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        return ExecuteUiStreamingAsync(chatRequest, cancellationToken);
    }

    private async IAsyncEnumerable<UIMessagePart> ExecuteUiStreamingAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var prompt = BuildPromptFromUiMessages(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BrowserUse requires non-empty input.");

        var metadata = chatRequest.GetProviderMetadata<BrowserUseRequestMetadata>(GetIdentifier()) ?? new BrowserUseRequestMetadata();
        var createRequest = new BrowserUseCreateSessionRequest
        {
            Task = prompt,
            Model = chatRequest.Model,
            MaxCostUsd = metadata.MaxCostUsd,
            ProfileId = metadata.ProfileId,
            WorkspaceId = metadata.WorkspaceId,
            ProxyCountryCode = metadata.ProxyCountryCode,
            OutputSchema = TryExtractStructuredOutputSchema(chatRequest.ResponseFormat),
            EnableRecording = metadata.EnableRecording ?? false,
            Skills = metadata.Skills,
            Agentmail = metadata.Agentmail,
            CacheScript = metadata.CacheScript
        };

        var textStarted = false;
        string? streamId = null;
        string? sessionToolCallId = null;
        var emittedAnyText = false;

        await foreach (var evt in StreamNativeTaskAsync(createRequest, cancellationToken))
        {
            switch (evt)
            {
                case BrowserUseNativeCreatedStreamEvent created:
                    streamId = $"msg_{created.Created.Id}";
                    sessionToolCallId = $"bu_{created.Created.Id}_session";

                    if (created.LiveSource is not null)
                        yield return created.LiveSource;

                    yield return new ToolCallPart
                    {
                        ToolCallId = sessionToolCallId,
                        ToolName = "browseruse_session",
                        ProviderExecuted = true,
                        Title = "BrowserUse session",
                        Input = new
                        {
                            sessionId = created.Created.Id,
                            liveUrl = created.Created.LiveUrl,
                            status = created.Created.Status,
                            model = created.Created.Model
                        }
                    };

                    break;

                case BrowserUseNativeArtifactStreamEvent artifactEvent:
                    if (artifactEvent.Artifact.Kind == "screenshot"
                        && artifactEvent.Artifact.Part is FileUIPart screenshotPart
                        && !string.IsNullOrWhiteSpace(sessionToolCallId)
                        && !string.IsNullOrWhiteSpace(screenshotPart.Url)
                        && !string.IsNullOrWhiteSpace(screenshotPart.MediaType))
                    {
                        yield return new ToolOutputAvailablePart
                        {
                            ToolCallId = sessionToolCallId,
                            ProviderExecuted = true,
                            Preliminary = true,
                            Output = new CallToolResult
                            {
                                Content = [ImageContentBlock.FromBytes(
                                    Convert.FromBase64String(screenshotPart.Url),
                                    screenshotPart.MediaType)]
                            }
                        };

                        break;
                    }

                    yield return artifactEvent.Artifact.Part;
                    break;

                case BrowserUseNativeActionStreamEvent actionEvent:
                    streamId ??= $"msg_{actionEvent.Action.SessionId}";

                    var toolTitle = chatRequest.Tools?.FirstOrDefault(t => t.Name == actionEvent.Action.ToolName)?.Title;

                    yield return new ToolCallPart
                    {
                        ToolCallId = actionEvent.Action.ToolCallId,
                        ToolName = actionEvent.Action.ToolName,
                        Input = actionEvent.Action.Input,
                        ProviderExecuted = true,
                        Title = toolTitle
                    };

                    yield return new ToolOutputAvailablePart
                    {
                        ToolCallId = actionEvent.Action.ToolCallId,
                        ProviderExecuted = true,
                        Output = actionEvent.Action.Output
                    };

                    if (actionEvent.Action.IsDone && !string.IsNullOrWhiteSpace(actionEvent.Action.DoneText))
                    {
                        emittedAnyText = true;

                        streamId ??= $"msg_{Guid.NewGuid():n}";
                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = actionEvent.Action.DoneText
                        };
                    }
                    break;

                case BrowserUseNativeTerminalStreamEvent terminalEvent:
                    streamId ??= $"msg_{terminalEvent.Terminal.Session.Id}";

                    if (!emittedAnyText && !string.IsNullOrWhiteSpace(terminalEvent.Terminal.OutputText))
                    {
                        if (!textStarted)
                        {
                            yield return streamId.ToTextStartUIMessageStreamPart();
                            textStarted = true;
                        }

                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = terminalEvent.Terminal.OutputText
                        };
                    }

                    if (textStarted && !string.IsNullOrWhiteSpace(streamId))
                        yield return streamId!.ToTextEndUIMessageStreamPart();

                    textStarted = false;

                    var session = terminalEvent.Terminal.Session;
                    var extraMetadata = new Dictionary<string, object>
                    {
                        ["gateway"] = CreateGatewayCostMetadata(session),
                        ["browseruse_session_id"] = session.Id,
                        ["browseruse_status"] = session.Status,
                        ["browseruse_is_success"] = session.IsTaskSuccessful ?? false,
                        ["browseruse_live_url"] = session.LiveUrl ?? string.Empty,
                        ["browseruse_screenshot_url"] = session.ScreenshotUrl ?? string.Empty,
                        ["browseruse_recording_count"] = session.RecordingUrls.Count,
                        ["browseruse_session_tool_call_id"] = sessionToolCallId ?? string.Empty
                    };

                    foreach (var artifact in terminalEvent.Artifacts)
                        yield return artifact.Part;

                    if (IsFinished(session.Status) && session.IsTaskSuccessful != false)
                    {
                        yield return "stop".ToFinishUIPart(
                            model: chatRequest.Model.ToModelId(GetIdentifier()),
                            outputTokens: session.TotalOutputTokens,
                            inputTokens: session.TotalInputTokens,
                            totalTokens: session.TotalInputTokens + session.TotalOutputTokens,
                            temperature: chatRequest.Temperature,
                            extraMetadata: extraMetadata);
                    }
                    else
                    {
                        var err = !string.IsNullOrWhiteSpace(terminalEvent.Terminal.OutputText)
                            ? terminalEvent.Terminal.OutputText
                            : $"BrowserUse session ended with status '{session.Status}'.";

                        if (!string.IsNullOrWhiteSpace(err))
                            yield return err!.ToErrorUIPart();

                        yield return "error".ToFinishUIPart(
                            model: chatRequest.Model.ToModelId(GetIdentifier()),
                            outputTokens: session.TotalOutputTokens,
                            inputTokens: session.TotalInputTokens,
                            totalTokens: session.TotalInputTokens + session.TotalOutputTokens,
                            temperature: chatRequest.Temperature,
                            extraMetadata: extraMetadata);
                    }

                    yield break;
            }
        }
    }

}
