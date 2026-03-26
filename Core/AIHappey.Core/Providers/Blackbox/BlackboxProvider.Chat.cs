using AIHappey.Core.AI;
using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using System.Text.Json;

namespace AIHappey.Core.Providers.BLACKBOX;

public partial class BLACKBOXProvider
{
    public async IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ApplyAuthHeader();

        if (IsNativeAgentModel(chatRequest.Model))
        {
            await foreach (var update in StreamNativeAgentUiAsync(chatRequest, cancellationToken))
                yield return update;

            yield break;
        }

        await foreach (var update in _client.CompletionsStreamAsync(chatRequest,
            url: "chat/completions",
            cancellationToken: cancellationToken))
            yield return update;
    }



    private async IAsyncEnumerable<UIMessagePart> StreamNativeAgentUiAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!TryResolveNativeAgent(chatRequest.Model, out var selectedAgent, out var selectedModel))
            throw new NotSupportedException(BuildUnsupportedNativeAgentModelMessage(chatRequest.Model));

        var prompt = BuildNativeTaskPromptFromUi(chatRequest.Messages);
        if (string.IsNullOrWhiteSpace(prompt))
            throw new InvalidOperationException("BLACKBOX native agent requires a non-empty prompt.");

        var created = await CreateNativeTaskAsync(new BlackboxNativeTaskCreateRequest
        {
            Prompt = prompt,
            SelectedAgent = selectedAgent,
            SelectedModel = selectedModel
        }, cancellationToken);

        var streamId = $"msg_{created.TaskId}";
        var textStarted = false;
        var emittedText = false;
        var terminalStatus = "error";

        await foreach (var evt in StreamNativeTaskEventsAsync(created.TaskId, fromIndex: 0, includeStatus: true, cancellationToken))
        {
            switch (evt.EventType)
            {
                case "log" when evt.Data is JsonElement logData && TryExtractLogPayload(logData, out var index, out var message, out var logType, out var contentType, out var step, out var agent):
                    {
                        if (logData.TryGetProperty("log", out var logElement) && logElement.ValueKind == JsonValueKind.Object)
                        {
                            yield return new DataUIPart
                            {
                                Type = "data-blackbox-log",
                                Id = $"bb_log_{created.TaskId}_{index}",
                                Data = new
                                {
                                    index,
                                    type = logType,
                                    contentType,
                                    step,
                                    agent,
                                    log = JsonSerializer.Deserialize<object>(logElement.GetRawText(), NativeJson)
                                },
                                Transient = true
                            };
                        }

                        if (string.IsNullOrWhiteSpace(message))
                            break;

                        if (!textStarted)
                        {
                            yield return new TextStartUIMessageStreamPart
                            {
                                Id = streamId
                            };
                            textStarted = true;
                        }

                        emittedText = true;
                        yield return new TextDeltaUIMessageStreamPart
                        {
                            Id = streamId,
                            Delta = message
                        };

                        break;
                    }

                case "status" when evt.Data is JsonElement statusData && TryExtractStatusPayload(statusData, out var statusValue, out var statusError):
                    {
                        terminalStatus = statusValue;
                        yield return new DataUIPart
                        {
                            Type = "data-blackbox-status",
                            Id = $"bb_status_{created.TaskId}_{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                            Data = new
                            {
                                status = statusValue,
                                error = statusError
                            },
                            Transient = true
                        };
                        break;
                    }

                case "error":
                    {
                        terminalStatus = "error";
                        var errorMessage = evt.Data is JsonElement errData
                                           && errData.ValueKind == JsonValueKind.Object
                                           && errData.TryGetProperty("error", out var errEl)
                                           && errEl.ValueKind == JsonValueKind.String
                            ? errEl.GetString()
                            : evt.RawData;

                        if (!string.IsNullOrWhiteSpace(errorMessage))
                        {
                            yield return new ErrorUIPart
                            {
                                ErrorText = errorMessage!
                            };
                        }

                        break;
                    }

                case "complete" when evt.Data is JsonElement completeData:
                    {
                        if (TryExtractStatusPayload(completeData, out var completeStatus, out _))
                            terminalStatus = completeStatus;

                        if (!emittedText)
                        {
                            var finalTask = await GetNativeTaskAsync(created.TaskId, cancellationToken);
                            var finalText = ExtractFinalTextFromTask(finalTask);
                            if (!string.IsNullOrWhiteSpace(finalText))
                            {
                                if (!textStarted)
                                {
                                    yield return new TextStartUIMessageStreamPart
                                    {
                                        Id = streamId
                                    };
                                    textStarted = true;
                                }

                                yield return new TextDeltaUIMessageStreamPart
                                {
                                    Id = streamId,
                                    Delta = finalText
                                };
                            }
                        }

                        if (textStarted)
                        {
                            yield return new TextEndUIMessageStreamPart
                            {
                                Id = streamId
                            };
                        }

                        yield return new FinishUIPart
                        {
                            FinishReason = GetStatusFinishReason(terminalStatus),
                            MessageMetadata = new Dictionary<string, object>
                            {
                                ["model"] = chatRequest.Model,
                                ["blackbox_task_id"] = created.TaskId,
                                ["blackbox_status"] = terminalStatus
                            }
                        };

                        yield break;
                    }
            }
        }


        if (textStarted)
        {
            yield return new TextEndUIMessageStreamPart
            {
                Id = streamId
            };
        }

        yield return new FinishUIPart
        {
            FinishReason = GetStatusFinishReason(terminalStatus),
            MessageMetadata = new Dictionary<string, object>
            {
                ["model"] = chatRequest.Model,
                ["blackbox_task_id"] = created.TaskId,
                ["blackbox_status"] = terminalStatus
            }
        };
    }
}
