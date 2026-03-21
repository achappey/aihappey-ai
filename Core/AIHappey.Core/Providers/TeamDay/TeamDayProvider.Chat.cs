using AIHappey.Common.Model;
using System.Runtime.CompilerServices;
using AIHappey.Vercel.Models;
using AIHappey.Vercel.Extensions;
using AIHappey.Common.Extensions;
using System.Text;
using System.Text.Json;

namespace AIHappey.Core.Providers.TeamDay;

public partial class TeamDayProvider
{
    public IAsyncEnumerable<UIMessagePart> StreamAsync(ChatRequest chatRequest,
       CancellationToken cancellationToken = default)
        => StreamUiNativeAsync(chatRequest, cancellationToken);


    private async IAsyncEnumerable<UIMessagePart> StreamUiNativeAsync(
        ChatRequest chatRequest,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chatRequest);

        var model = NormalizeAgentModelId(chatRequest.Model);
        var metadata = ReadChatMetadata(chatRequest);
        var prompt = ApplyStructuredOutputInstructions(BuildPromptFromUiMessages(chatRequest.Messages), chatRequest.ResponseFormat);

        var streamId = Guid.NewGuid().ToString("n");
        var textStarted = false;
        var buffer = new StringBuilder();
        object? usage = null;
        string? failureMessage = null;
        string? chatId = null;
        string? sessionId = null;

        await foreach (var evt in StreamAgentExecutionAsync(model, prompt, metadata, cancellationToken))
        {
            switch (evt)
            {
                case TeamDayMetaStreamEvent meta:
                    if (!string.IsNullOrWhiteSpace(meta.ExecutionId))
                        streamId = meta.ExecutionId!;

                    if (!string.IsNullOrWhiteSpace(meta.ChatId))
                        chatId = meta.ChatId;

                    if (!string.IsNullOrWhiteSpace(meta.SessionId))
                        sessionId = meta.SessionId;
                    break;

                case TeamDayDeltaStreamEvent delta when !string.IsNullOrEmpty(delta.Text):
                    if (!textStarted)
                    {
                        yield return streamId.ToTextStartUIMessageStreamPart();
                        textStarted = true;
                    }

                    buffer.Append(delta.Text);
                    yield return new TextDeltaUIMessageStreamPart
                    {
                        Id = streamId,
                        Delta = delta.Text
                    };
                    break;

                case TeamDayResultStreamEvent result:
                    if (!string.IsNullOrWhiteSpace(result.SessionId))
                        sessionId = result.SessionId;

                    if (result.Usage is not null)
                        usage = result.Usage;
                    break;

                case TeamDayErrorStreamEvent error:
                    failureMessage = error.Message;
                    break;
            }
        }

        if (textStarted)
            yield return streamId.ToTextEndUIMessageStreamPart();

        var finalText = buffer.ToString();
        var structured = TryParseStructuredOutput(finalText, chatRequest.ResponseFormat);
        if (structured is not null)
        {
            var schema = chatRequest.ResponseFormat.GetJSONSchema();
            yield return new DataUIPart
            {
                Type = $"data-{schema?.JsonSchema?.Name ?? "unknown"}",
                Data = structured
            };
        }

        if (!string.IsNullOrWhiteSpace(failureMessage))
            yield return failureMessage.ToErrorUIPart();

        yield return (failureMessage is null ? "stop" : "error").ToFinishUIPart(
            chatRequest.Model,
            outputTokens: TryGetUsageInt(usage, "completion_tokens") ?? 0,
            inputTokens: TryGetUsageInt(usage, "prompt_tokens") ?? 0,
            totalTokens: TryGetUsageInt(usage, "total_tokens") ?? 0,
            temperature: chatRequest.Temperature,
            extraMetadata: BuildFinishMetadata(streamId, chatId, sessionId));
    }

    private static string BuildPromptFromUiMessages(IEnumerable<UIMessage>? messages)
    {
        var lines = new List<string>();

        foreach (var message in messages ?? [])
        {
            var parts = new List<string>();
            foreach (var part in message.Parts ?? [])
            {
                switch (part)
                {
                    case TextUIPart text when !string.IsNullOrWhiteSpace(text.Text):
                        parts.Add(text.Text);
                        break;

                    case ReasoningUIPart reasoning when !string.IsNullOrWhiteSpace(reasoning.Text):
                        parts.Add(reasoning.Text);
                        break;

                    case FileUIPart file when !string.IsNullOrWhiteSpace(file.Url):
                        parts.Add($"[{file.MediaType}] {file.Url}");
                        break;

                    case ToolInvocationPart toolInvocation:
                        parts.Add($"tool:{toolInvocation.ToolCallId}:{toolInvocation.Title ?? toolInvocation.Type}:{SerializeCompact(toolInvocation.Input)}");
                        if (toolInvocation.Output is not null)
                            parts.Add($"tool_output:{toolInvocation.ToolCallId}:{SerializeCompact(toolInvocation.Output)}");
                        break;
                }
            }

            var joined = string.Join("\n", parts.Where(static p => !string.IsNullOrWhiteSpace(p)));
            if (!string.IsNullOrWhiteSpace(joined))
                lines.Add($"{message.Role}: {joined}");
        }

        return string.Join("\n\n", lines);
    }


    private static Dictionary<string, object> BuildFinishMetadata(string executionId, string? chatId, string? sessionId)
    {
        var metadata = new Dictionary<string, object>
        {
            ["teamdayExecutionId"] = executionId
        };

        if (!string.IsNullOrWhiteSpace(chatId))
            metadata["teamdayChatId"] = chatId!;

        if (!string.IsNullOrWhiteSpace(sessionId))
            metadata["teamdaySessionId"] = sessionId!;

        return metadata;
    }


    private static object? TryParseStructuredOutput(string text, object? responseFormat)
    {
        if (responseFormat is null || string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = NormalizePotentialJson(text);

        try
        {
            return JsonSerializer.Deserialize<object>(normalized, Json);
        }
        catch
        {
            return null;
        }
    }

    
    private static TeamDayRequestMetadata ReadChatMetadata(ChatRequest request)
        => request.GetProviderMetadata<TeamDayRequestMetadata>(nameof(TeamDay).ToLowerInvariant()) ?? new TeamDayRequestMetadata();


}
